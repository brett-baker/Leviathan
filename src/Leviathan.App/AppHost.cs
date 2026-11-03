using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Interop;
using Leviathan.Providers.Cloud;
using Leviathan.Providers.Local;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.NewMenu;
using Leviathan.ViewModels.Sidebar;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;
using Leviathan.ViewModels.Windows;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace Leviathan.App;

/// <summary>Runs view-model work on the WinUI dispatcher (spec §3.2: results reach the UI thread in batches).</summary>
internal sealed class DispatcherQueueUiContext(DispatcherQueue queue) : IUiContext
{
    public void Post(Action action) => queue.TryEnqueue(() =>
    {
        using var scope = UiTrace.Begin($"UI post {action.Target?.GetType().Name}.{action.Method.Name}", thresholdMs: 5);
        action();
    });
}

/// <summary>
/// The services every window shares: location providers, the window manager, the launcher and the platform lookups. Built
/// once at startup; the composition root of the UI process.
/// </summary>
internal sealed class AppHost
{
    private readonly object _backgroundLock = new();
    private IReadOnlyList<LocationPath> _favorites = [];
    private IReadOnlyList<CloudProviderInfo> _cloud = [];

    private AppHost(
        WindowManager windows,
        AddressResolver resolver,
        IFileLauncher launcher,
        IKnownFolders knownFolders,
        IVolumeInfo volumes,
        KeyMap keys,
        LocationPath home,
        RecentLocations recent,
        AddressCompleter completer,
        SubfolderMenu subfolders,
        IThumbnailCache thumbnails,
        FavoritesList favorites,
        ISubfolderSource sidebarSource,
        NewItemService newItems,
        ViewOptions viewOptions,
        RenameService renamer)
    {
        Renamer = renamer;
        NewItems = newItems;
        ViewOptions = viewOptions;
        Thumbnails = thumbnails;
        Favorites = favorites;
        SidebarSource = sidebarSource;
        Windows = windows;
        Resolver = resolver;
        Launcher = launcher;
        KnownFolders = knownFolders;
        Volumes = volumes;
        Keys = keys;
        Home = home;
        Recent = recent;
        AddressBar = new AddressBarServices(completer, subfolders, recent.Snapshot, () => Volatile.Read(ref _favorites), IsCloudFolder);
    }

    public WindowManager Windows { get; }

    public AddressResolver Resolver { get; }

    public IFileLauncher Launcher { get; }

    public IKnownFolders KnownFolders { get; }

    public IVolumeInfo Volumes { get; }

    public KeyMap Keys { get; }

    /// <summary>The New menu: built-in items, apps registered through ShellNew and the user's templates (spec §6.1).</summary>
    public NewItemService NewItems { get; }

    /// <summary>Renames files and folders for the inline rename box.</summary>
    public RenameService Renamer { get; }

    /// <summary>Carries out drops and pastes. Until the copy and move slice (E2) it says so and changes nothing.</summary>
    public WorkerTransfers Transfers { get; private init; } = null!;

    /// <summary>What Ctrl+Z and Ctrl+Y undo and redo, for all windows (spec §12.5); it survives a restart.</summary>
    public Leviathan.ViewModels.Undo.UndoService Undo { get; private init; } = null!;

    private readonly Lazy<Leviathan.Engine.Supervision.ShellHostConnection> _shellHost = new(() => new Leviathan.Engine.Supervision.ShellHostConnection(ShellHostLauncher()));

    /// <summary>
    /// The out-of-process home of shell extensions (spec §2.2). Nothing starts until the first call to
    /// <see cref="Leviathan.Engine.Supervision.ShellHostConnection.GetServiceAsync"/>, which the right-click menu makes (slice F3).
    /// </summary>
    public Leviathan.Engine.Supervision.ShellHostConnection ShellHost => _shellHost.Value;

    private static Leviathan.Engine.Supervision.ExecutableLauncher ShellHostLauncher()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Leviathan.ShellHost.exe");
        return File.Exists(exe)
            ? new Leviathan.Engine.Supervision.ExecutableLauncher(exe)
            : new Leviathan.Engine.Supervision.ExecutableLauncher("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Leviathan.ShellHost.dll")}\"");
    }

    /// <summary>The user's settings (<c>settings.json</c>); loaded by <see cref="PlanStartupAsync"/>.</summary>
    public SettingsStore Settings { get; private init; } = null!;

    /// <summary>Keeps Last session current (spec §17.1).</summary>
    public SessionRecorder Recorder { get; private init; } = null!;

    /// <summary>Saves and opens named startup profiles (spec §17.1).</summary>
    public ProfileService Profiles { get; private init; } = null!;

    private JsonFileStore<SessionFile> SessionStore { get; init; } = null!;

    /// <summary>Remembered folder listings, shown at once and checked in the background (spec §3.1).</summary>
    public ListingCache Listings { get; private init; } = null!;

    /// <summary>Keeps the saved folder views (columns, sort, layout) between runs.</summary>
    private ViewRulesPersistence ViewRulesStore { get; init; } = null!;

    /// <summary>
    /// Reads the settings, the profiles and Last session off the UI thread and decides what to open (spec §17.1). Reading
    /// problems never stop startup: a missing or damaged file means defaults and a fresh window.
    /// </summary>
    public async Task<StartupPlan> PlanStartupAsync(CancellationToken cancellationToken)
    {
        var settings = await Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await Profiles.Catalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        await ViewRulesStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ViewRulesStore.Start();
        var last = await SessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return StartupPlanner.Decide(settings.General, last, Profiles.Catalog);
    }

    private WindowsFileClipboard? _clipboard;

    /// <summary>Cut, copy and paste of files through the Windows clipboard (spec §6.1). Made on first use, on the UI thread, which the WinRT clipboard requires.</summary>
    public WindowsFileClipboard FileClipboard => _clipboard ??= new WindowsFileClipboard();

    /// <summary>Which drives have their summary card hidden (spec §8.7).</summary>
    public Leviathan.ViewModels.Drives.DriveCardPreferences DriveCards { get; } = new(new JsonDriveCardStorage());

    /// <summary>Whether hidden and system items are listed; one switch for all windows.</summary>
    public ViewOptions ViewOptions { get; }

    /// <summary>The pinned folders shown under Favorites, shared by all windows.</summary>
    public FavoritesList Favorites { get; }

    /// <summary>Lists the folders below a sidebar node.</summary>
    public ISubfolderSource SidebarSource { get; }

    /// <summary>Thumbnails and icons for the Thumbnails layout, shared by all windows (spec §3.1: size-capped, off the UI thread).</summary>
    public IThumbnailCache Thumbnails { get; }

    /// <summary>Folders visited recently (shared by all tabs and windows).</summary>
    public RecentLocations Recent { get; }

    /// <summary>What the address bar needs: autocomplete, subfolder lists, recent and favorite folders.</summary>
    internal AddressBarServices AddressBar { get; }

    /// <summary>Raised on the thread pool once the cloud sync folders have been detected.</summary>
    public event EventHandler? CloudFoldersChanged;

    /// <summary>The cloud sync clients found so far (empty until detection finishes).</summary>
    public IReadOnlyList<CloudProviderInfo> CloudProviders => Volatile.Read(ref _cloud);

    /// <summary>True when <paramref name="location"/> is inside a cloud sync client's folder.</summary>
    public bool IsCloudFolder(LocationPath location) => Volatile.Read(ref _cloud).Any(c => c.SyncFolder.Contains(location));

    /// <summary>
    /// Reads the things the UI must not wait for: the favorite folders (known folders, registry) and the installed cloud sync
    /// clients (registry and their config files). Called once at startup, off the UI thread.
    /// </summary>
    public async Task LoadBackgroundDataAsync(CancellationToken cancellationToken)
    {
        var favorites = await Task.Run(
            () => new[] { KnownFolder.Home, KnownFolder.Desktop, KnownFolder.Documents, KnownFolder.Downloads, KnownFolder.Pictures, KnownFolder.Videos, KnownFolder.Music }
                .Select(KnownFolders.Get)
                .OfType<LocationPath>()
                .ToList(),
            cancellationToken);
        Volatile.Write(ref _favorites, favorites);

        // The registry scan for ShellNew entries is the slow part of the New menu; do it now so the menu opens at once.
        NewItems.Prime();

        var cloud = await CloudProviderCatalog.ForThisMachine().DetectAsync(cancellationToken);
        Volatile.Write(ref _cloud, cloud);
        CloudFoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a new tab opens. The user profile folder; resolved from the environment, so no disk access at startup.</summary>
    public LocationPath Home { get; }

    public static AppHost Create(IUiContext ui)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var home = LocationPath.TryParse(profile, out var parsed) ? parsed : LocationPath.Parse(@"C:\");
        var providers = new LocationProviderRegistry([new LocalLocationProvider()]);
        var recent = new RecentLocations();
        var lister = new ProviderFolderLister(providers);
        var queue = DispatcherQueue.GetForCurrentThread();
        var worker = new Lazy<WorkerConnection>(() => new WorkerConnection(new DispatcherQueueUiDispatcher(queue), []));
        var sizes = new WorkerFolderSizes(() => worker.Value);
        var viewOptions = new ViewOptions();
        var viewRules = new FolderViewRules();
        var listings = new ListingCache();
        var services = new TabServices(providers, ui, viewRules, TimeProvider.System, () => home, recent, sizes, viewOptions, listings);
        var disk = new FileSystem();
        var newItems = new NewItemService(new ShellNewCatalog(new WindowsRegistryView(), disk, NewMenuOptions.ForThisMachine()), new NewItemCreator(disk));
        var transfers = new WorkerTransfers(() => worker.Value);
        var undo = new Leviathan.ViewModels.Undo.UndoService(new JsonUndoStorage(), new WorkerUndoActions(() => worker.Value));
        transfers.DeleteCompleted += (_, moves) => undo.RecordDelete(moves);

        // Our own copies, moves and deletes change folders the cache remembers (spec §3.1): forget them before any view reloads.
        transfers.Finished += (_, done) =>
        {
            if (done.Destination is { } to)
            {
                listings.Invalidate(to, includeDescendants: true);
            }

            foreach (var source in done.Sources)
            {
                listings.Invalidate(source, includeDescendants: true);
                if (source.Parent is { } parent)
                {
                    listings.Invalidate(parent);
                }
            }
        };
        var windows = new WindowManager(services);

        // LEVIATHAN_DATA_DIR moves settings, Last session and profiles elsewhere: a portable copy, or a test run that must not
        // touch the real ones.
        var data = Environment.GetEnvironmentVariable("LEVIATHAN_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leviathan");
        var sessionStore = new JsonFileStore<SessionFile>(disk, Path.Combine(data, "session.json"));
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(disk, Path.Combine(data, "profiles.json")));
        var recorder = new SessionRecorder(windows, sessionStore, ui);
        return new AppHost(
            windows,
            new AddressResolver(new FileSystemEntryProbe(new FileSystem())),
            new WindowsFileLauncher(),
            new WindowsKnownFolders(),
            new WindowsVolumeInfo(),
            WindowCommands.DefaultKeyMap(),
            home,
            recent,
            new AddressCompleter(lister),
            new SubfolderMenu(lister),
            new ThumbnailCache(new ShellThumbnailSource()),
            new FavoritesList(new JsonFavoritesStorage()),
            new ProviderSubfolderSource(providers),
            newItems,
            viewOptions,
            new RenameService(disk))
        {
            Transfers = transfers,
            Undo = undo,
            Listings = listings,
            Settings = new SettingsStore(disk, Path.Combine(data, "settings.json")),
            ViewRulesStore = new ViewRulesPersistence(viewRules, new JsonFileStore<ViewRulesFile>(disk, Path.Combine(data, "viewrules.json"))),
            SessionStore = sessionStore,
            Recorder = recorder,
            Profiles = new ProfileService(windows, catalog, recorder),
        };
    }
}

/// <summary>Turns WinUI key presses into <see cref="KeyGesture"/>s for the key map.</summary>
internal static class KeyTranslator
{
    public static KeyGesture? ToGesture(VirtualKey key)
    {
        var name = NameOf(key);
        if (name is null)
        {
            return null;
        }

        var modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= KeyModifiers.Ctrl;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        return KeyGesture.Create(modifiers, name);
    }

    /// <summary>The number 1 to 9 for Ctrl+1..9 (tab shortcuts), or 0.</summary>
    public static int DigitOf(VirtualKey key) => key is >= VirtualKey.Number1 and <= VirtualKey.Number9 ? key - VirtualKey.Number0 : 0;

    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static string? NameOf(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => key.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)(key - VirtualKey.Number0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= VirtualKey.F1 and <= VirtualKey.F24 => key.ToString(),
        VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down => key.ToString(),
        VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Escape or VirtualKey.Delete or VirtualKey.Space or VirtualKey.Home or VirtualKey.End
            or VirtualKey.Insert or VirtualKey.PageUp or VirtualKey.PageDown => key.ToString(),
        VirtualKey.Back => "Backspace",
        _ => null,
    };
}
