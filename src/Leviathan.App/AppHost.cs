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
        var services = new TabServices(providers, ui, new FolderViewRules(), TimeProvider.System, () => home, recent, sizes, viewOptions);
        var disk = new FileSystem();
        var newItems = new NewItemService(new ShellNewCatalog(new WindowsRegistryView(), disk, NewMenuOptions.ForThisMachine()), new NewItemCreator(disk));
        var transfers = new WorkerTransfers(() => worker.Value);
        var undo = new Leviathan.ViewModels.Undo.UndoService(new JsonUndoStorage(), new WorkerUndoActions(() => worker.Value));
        transfers.DeleteCompleted += (_, moves) => undo.RecordDelete(moves);
        return new AppHost(
            new WindowManager(services),
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
