using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Discovery;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Data;
using Leviathan.Interop;
using Leviathan.Providers.Cloud;
using Leviathan.Providers.Local;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Activity;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Hub;
using Leviathan.ViewModels.Icons;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Triage;
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

/// <summary>
/// The services every window shares: location providers, the window manager, the launcher and the platform lookups. Built
/// once at startup; the composition root of the UI process.
/// </summary>
internal sealed class AppHost
{
    private readonly object _backgroundLock = new();
    private IReadOnlyList<LocationPath> _favorites = [];
    private IReadOnlyList<CloudProviderInfo> _cloud = [];
    private IReadOnlyDictionary<CloudProviderInfo, CloudSyncState> _cloudStates = new Dictionary<CloudProviderInfo, CloudSyncState>();
    private readonly Leviathan.Providers.Cloud.ProcessCloudSyncProbe _cloudProbe = new();

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

    public KeyMap Keys { get; private set; }

    /// <summary>The keys of triage mode (spec §10.2) with the settings file's changes.</summary>
    public KeyMap TriageKeys { get; private set; } = TriageCommands.DefaultKeyMap();

    /// <summary>Entries of the settings file's <c>KeyBindings</c> that could not be used, for the Keyboard page.</summary>
    public IReadOnlyList<string> KeyProblems { get; private set; } = [];

    /// <summary>Keys the settings file took from one command and gave to another.</summary>
    public IReadOnlyList<string> KeyNotes { get; private set; } = [];

    private string _appliedKeys = string.Empty;

    /// <summary>The New menu: built-in items, apps registered through ShellNew and the user's templates (spec §6.1).</summary>
    public NewItemService NewItems { get; }

    /// <summary>Renames files and folders for the inline rename box.</summary>
    public RenameService Renamer { get; }

    /// <summary>Carries out drops and pastes. Until the copy and move slice (E2) it says so and changes nothing.</summary>
    public WorkerTransfers Transfers { get; private init; } = null!;

    /// <summary>The saved sync pairs (spec 15.5).</summary>
    public Leviathan.ViewModels.Sync.SyncPairStore SyncPairs { get; private init; } = null!;

    /// <summary>Checks the saved sync pairs on their schedule.</summary>
    public Leviathan.ViewModels.Sync.SyncPairChecker SyncChecker { get; private init; } = null!;

    /// <summary>Extract and compress jobs (spec 15.4).</summary>
    public WorkerArchives Archives { get; private init; } = null!;

    /// <summary>What running deletes, moves and copies will do to the rows, shown at once when the setting says so.</summary>
    public PendingChanges Pending { get; private init; } = null!;

    /// <summary>What Ctrl+Z and Ctrl+Y undo and redo, for all windows (spec §12.5); it survives a restart.</summary>
    public Leviathan.ViewModels.Undo.UndoService Undo { get; private init; } = null!;

    /// <summary>
    /// Makes the actions of one triage window. <c>destination</c> says where kept files go right now (the other pane of a split tab), or
    /// returns null to use the keep folder of the settings; it is asked each time, so navigating the other pane during triage counts.
    /// </summary>
    public Func<Func<string?>, ITriageActions> TriageActionsFor { get; private init; } = null!;

    /// <summary>Colour tags on the selection in the file view (spec §11.1): keys 1 to 9 and Ctrl+0.</summary>
    public Leviathan.ViewModels.Tags.FileTagging Tagging { get; private init; } = null!;

    /// <summary>Short notes attached to folders, kept in the data folder and edited in the details pane (spec §15.6).</summary>
    public Leviathan.ViewModels.Notes.FolderNotes Notes { get; } = new(new JsonFolderNotesStorage());

    /// <summary>Where colour tags are read and written (the details pane reads the selected file's tags from it).</summary>
    public Leviathan.ViewModels.Triage.ITagStore TagStore { get; private init; } = null!;

    /// <summary>The SFTP, FTP and FTPS servers under Network (spec 7.1).</summary>
    public Leviathan.ViewModels.Sidebar.RemoteConnectionList RemoteConnections { get; private init; } = null!;

    /// <summary>Passwords of those servers, kept in Windows Credential Manager.</summary>
    public Leviathan.Providers.Remote.IRemoteSecrets RemoteSecrets { get; private init; } = null!;

    /// <summary>Lists those servers' folders.</summary>
    public Leviathan.Providers.Remote.RemoteLocationProvider RemoteProvider { get; private init; } = null!;

    /// <summary>Asks the user whether to trust a server seen for the first time.</summary>
    public TrustPrompt Trust { get; private init; } = null!;

    /// <summary>The cached lists of the archives being browsed (spec §15.4); the preview pane lists 7z and rar files from it.</summary>
    public Leviathan.Providers.Archive.ArchiveIndex ArchiveIndex { get; private init; } = null!;

    /// <summary>Unpacks an item inside an archive to the scratch folder so a renderer or program that wants a path can use it.</summary>
    public Leviathan.Providers.Archive.ArchiveStager ArchiveStager { get; private init; } = null!;

    /// <summary>Where search results report their progress to, and where the search gets tags and media information (spec 13).</summary>
    public SearchHooks SearchHooks { get; private init; } = null!;

    /// <summary>Scans folders for the space map and keeps the last scan of each (spec 14).</summary>
    public Leviathan.SpaceMap.SpaceScanner SpaceScanner { get; private init; } = null!;

    /// <summary>Keeps the folders chosen for the search index listed (spec 13).</summary>
    public Leviathan.Search.IndexManager SearchIndex { get; private init; } = null!;

    /// <summary>
    /// Starts or stops indexing <paramref name="folder"/> for search and returns true when it is indexed afterwards. A folder inside an indexed folder is already covered
    /// (nothing changes and the answer is true); an indexed folder that is chosen again is removed.
    /// </summary>
    public bool ToggleSearchIndex(LocationPath folder)
    {
        if (SearchIndex.Roots.Contains(folder))
        {
            SearchIndex.Remove(folder);
            return false;
        }

        if (SearchIndex.IsIndexed(folder))
        {
            return true;
        }

        SearchIndex.Add(folder);
        _ = Task.Run(async () =>
        {
            try
            {
                await SearchIndex.CrawlNowAsync(folder, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Removed again before it finished.
            }
        });
        return true;
    }

    private readonly Lazy<Leviathan.Engine.Supervision.ShellHostConnection> _shellHost = new(() => new Leviathan.Engine.Supervision.ShellHostConnection(Leviathan.Engine.Supervision.HelperProcess.LauncherFor("Leviathan.ShellHost")));

    /// <summary>
    /// The out-of-process home of shell extensions (spec §2.2). Nothing starts until the first call to
    /// <see cref="Leviathan.Engine.Supervision.ShellHostConnection.GetServiceAsync"/>, which the right-click menu makes (slice F3).
    /// </summary>
    public Leviathan.Engine.Supervision.ShellHostConnection ShellHost => _shellHost.Value;

    private readonly Lazy<Leviathan.Engine.Supervision.MediaHostConnection> _mediaHost = new(() => new Leviathan.Engine.Supervision.MediaHostConnection(Leviathan.Engine.Supervision.HelperProcess.LauncherFor("Leviathan.Media")));

    /// <summary>
    /// The out-of-process home of ffprobe metadata (spec §2.2, §9.3). Nothing starts until the first call to
    /// <see cref="Leviathan.Engine.Supervision.MediaHostConnection.GetServiceAsync"/>, which a folder that shows media information makes.
    /// </summary>
    public Leviathan.Engine.Supervision.MediaHostConnection MediaHost => _mediaHost.Value;

    /// <summary>The user's settings (<c>settings.json</c>); loaded by <see cref="PlanStartupAsync"/>.</summary>
    public SettingsStore Settings { get; private init; } = null!;

    private IQuickTargets? _quickTargets;

    /// <summary>The nine quick move and copy targets, kept in the settings (spec §15.6).</summary>
    public IQuickTargets QuickTargets => _quickTargets ??= new SettingsQuickTargets(Settings);

    /// <summary>Keeps Last session current (spec §17.1).</summary>
    public SessionRecorder Recorder { get; private init; } = null!;

    /// <summary>Saves and opens named startup profiles (spec §17.1).</summary>
    public ProfileService Profiles { get; private init; } = null!;

    private JsonFileStore<SessionFile> SessionStore { get; init; } = null!;

    /// <summary>Remembered folder listings, shown at once and checked in the background (spec §3.1).</summary>
    public ListingCache Listings { get; private init; } = null!;

    internal WorkerFolderSizes Sizes { get; init; } = null!;

    /// <summary>Submits the bin purge policy to the worker (spec 12.5); the purge schedule uses it.</summary>
    internal WorkerBinService BinService { get; init; } = null!;

    /// <summary>Git status of the folders on show (spec §15.6).</summary>
    internal Leviathan.ViewModels.Git.GitStatusService Git { get; private init; } = null!;

    private NetworkThumbnailGate ThumbnailGate { get; init; } = null!;

    /// <summary>Asks the media process for contact sheets (spec §10.4).</summary>
    internal ContactSheetClient SheetClient { get; init; } = null!;

    /// <summary>The strips hover scrub slides over (spec §10.5); UI thread only.</summary>
    internal ScrubStripCache Scrub { get; init; } = null!;

    /// <summary>The folder of contact sheets being made in the background, if any.</summary>
    internal ContactSheetBatch SheetBatch { get; init; } = null!;

    private const string RepositoryUrl = "https://github.com/brett-baker/Leviathan";

    /// <summary>Downloads a found update and restarts into it when the user chooses (release plan §4).</summary>
    internal Leviathan.ViewModels.Updates.UpdateInstaller UpdateInstaller { get; private init; } = null!;

    // One Velopack source serves both the check and the install, because the install needs what the check found.
    private static (Leviathan.ViewModels.Updates.UpdateChecker Checker, Leviathan.ViewModels.Updates.UpdateInstaller Installer) NewUpdates(Func<AppHost> late)
    {
        var build = Leviathan.Diagnostics.Crash.CrashReportBuilder.ReadBuildInfo(typeof(AppHost).Assembly);
        var source = new VelopackUpdateSource(build.RepositoryUrl ?? RepositoryUrl);
        var checker = new Leviathan.ViewModels.Updates.UpdateChecker(source, Leviathan.ViewModels.Settings.AppVersion.Parse(build), () => late().Settings.Current.Updates);
        var installer = new Leviathan.ViewModels.Updates.UpdateInstaller(
            source,
            new HelperShutdown(ct => late().StopHelpersAsync(ct)),
            () => new Leviathan.ViewModels.Updates.JobLoad(late().Operations.ActiveCount, late().Operations.QueuedCount));
        return (checker, installer);
    }

    private sealed class HelperShutdown(Func<CancellationToken, Task> stop) : Leviathan.ViewModels.Updates.IHelperStopper
    {
        public Task StopAllAsync(CancellationToken cancellationToken) => stop(cancellationToken);
    }

    // The worker would otherwise outlive the UI while it has jobs (spec 4.2) and hold files the update replaces. Jobs are journaled and resume
    // when the new version starts it again (spec §3.4). The connections are closed first, so the supervisors do not start the helpers again.
    private async Task StopHelpersAsync(CancellationToken cancellationToken)
    {
        if (WorkerLazy.IsValueCreated)
        {
            await WorkerLazy.Value.DisposeAsync();
        }

        if (_shellHost.IsValueCreated)
        {
            await _shellHost.Value.DisposeAsync();
        }

        if (_mediaHost.IsValueCreated)
        {
            await _mediaHost.Value.DisposeAsync();
        }

        await Task.Run(() => Leviathan.Engine.Supervision.HelperStopper.StopAllAsync(AppContext.BaseDirectory, cancellationToken), cancellationToken);
    }

    // The first check waits until well after the first folder is on screen (spec §3.1); a newer version is written to the Activity tab once.
    private void StartUpdateChecks()
    {
        string? announced = null;
        Updates.Changed += (_, _) =>
        {
            if (Updates.LastError is { } error)
            {
                UiTrace.Mark($"update check failed: {error}");
            }

            if (Updates.State == Leviathan.ViewModels.Updates.UpdateState.Available && Updates.Offer is { } offer && offer.Version != announced)
            {
                announced = offer.Version;
                Activity.Note($"Leviathan {offer.Version} is ready. Open Settings > Advanced to see it.", ActivityKind.Message);
            }
        };

        _ = Updates.RunAsync(CancellationToken.None);
    }

    /// <summary>The folder a new tab opens: the one chosen in General, else Home.</summary>
    public LocationPath NewTabLocation() =>
        Settings.Current.General.NewTabLocation is { Length: > 0 } chosen && LocationPath.TryParse(chosen, out var parsed) ? parsed : Home;

    /// <summary>
    /// Applies the settings to the parts of the app they change (spec 17.2: every setting takes effect without a restart), now and
    /// whenever they change: what the file list shows, folder sizes, thumbnails, how much runs at once, the last-tab rule, and the
    /// icon pack. A change made in the View menu is written back, so the two never disagree.
    /// </summary>
    private void StartApplyingSettings(IUiContext ui)
    {
        Settings.Changed += (_, changed) => ui.Post(() => ApplySettings(changed));
        ViewOptions.PropertyChanged += (_, _) =>
        {
            var wanted = (ViewOptions.ShowHidden, ViewOptions.ShowSystem);
            var files = Settings.Current.Files;
            if (wanted != (files.ShowHidden, files.ShowSystem))
            {
                _ = SaveQuietlyAsync(s => s with { Files = s.Files with { ShowHidden = wanted.ShowHidden, ShowSystem = wanted.ShowSystem } });
            }
        };
        Windows.WindowOpened += (_, window) => window.Tabs.LastTabResetsToHome = Settings.Current.Tabs.LastTabResetsToHome;
    }

    /// <summary>Applies <paramref name="settings"/>. On the UI thread.</summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Icons.UsePack(settings.Appearance.IconPack);
        if (ViewOptions.ShowHidden != settings.Files.ShowHidden)
        {
            ViewOptions.ShowHidden = settings.Files.ShowHidden;
        }

        if (ViewOptions.ShowSystem != settings.Files.ShowSystem)
        {
            ViewOptions.ShowSystem = settings.Files.ShowSystem;
        }

        ApplyKeyBindings(settings.KeyBindings);
        ApplyAppearance(settings.Appearance);
        Sizes.Enabled = settings.Files.ShowFolderSizes;
        Sizes.NetworkEnabled = settings.Performance.FolderSizesOnNetwork;
        ThumbnailGate.AllowNetwork = settings.Performance.ThumbnailsOnNetwork;
        foreach (var window in Windows.Windows)
        {
            window.Tabs.LastTabResetsToHome = settings.Tabs.LastTabResetsToHome;
        }

        if (WorkerLazy.IsValueCreated)
        {
            _ = ApplyLimitsAsync(WorkerLazy.Value);
        }
    }

    /// <summary>Raised on the UI thread when the theme, the density, the accent, the backdrop or the icon size changed; each window redraws itself.</summary>
    public event EventHandler? AppearanceChanged;

    private AppearanceKey? _appliedLook;

    // The look of the app (spec 17.2 Appearance). The accent and the sizes are app-wide and set here; the theme and the backdrop
    // belong to each window, which listens to AppearanceChanged.
    private void ApplyAppearance(AppearanceSettings look)
    {
        var key = AppearanceKey.From(look);
        if (_appliedLook == key)
        {
            return;
        }

        var accentChanged = key.AccentChangedFrom(_appliedLook);
        _appliedLook = key;
        var metrics = AppearanceMetrics.For(look.Density, look.IconSize);
        GridRow.RowHeight = metrics.DetailsRowHeight;
        GridRow.IconSize = metrics.IconSize;
        SidebarFormat.VerticalPadding = metrics.SidebarRowPadding;
        if (accentChanged)
        {
            AccentResources.Apply(look.Accent);
        }

        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    // The default keys with the user's changes from the settings file (spec 17.3). Only rebuilt when the file's KeyBindings changed.
    private void ApplyKeyBindings(IReadOnlyDictionary<string, string> bindings)
    {
        var signature = AppKeyBindings.SignatureOf(bindings);
        if (signature == _appliedKeys)
        {
            return;
        }

        _appliedKeys = signature;
        var built = AppKeyBindings.Build(bindings);
        Keys = built.Keys;
        TriageKeys = built.TriageKeys;
        KeyProblems = built.Problems;
        KeyNotes = built.Notes;
    }

    /// <summary>Tells the worker how many jobs may run at once.</summary>
    public async Task ApplyLimitsAsync(WorkerConnection connection)
    {
        var performance = Settings.Current.Performance;
        try
        {
            await connection.SetLimitsAsync(new ConcurrencyLimits(performance.MaxRunningJobs, performance.MaxPerDevice, performance.MaxBackgroundJobs));
            await connection.SetBandwidthAsync(performance.ToBandwidth());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException)
        {
            // The worker is not reachable right now; the limits are sent again when it connects.
        }
    }

    private async Task SaveQuietlyAsync(Func<AppSettings, AppSettings> change)
    {
        try
        {
            await Settings.UpdateAsync(change, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A setting that could not be saved still applies for this run.
        }
    }

    /// <summary>Where the app keeps its settings, session, profiles and caches; <c>LEVIATHAN_DATA_DIR</c> moves it (a portable copy, or a test run).</summary>
    public static string DataFolder() => Leviathan.Core.Platform.DataLocation.Root();

    /// <summary>The settings file, for "Show the settings file".</summary>
    public string SettingsPath { get; private init; } = string.Empty;

    /// <summary>Background jobs, thumbnails and the state of the worker and shell host, for the Background tab (spec 16.1).</summary>
    public BackgroundModel Background { get; private init; } = null!;

    /// <summary>The Soft-delete bin: what was deleted from network shares, for the bin tab of the status hub (spec 12.5).</summary>
    public BinModel Bin { get; private init; } = null!;

    private Lazy<WorkerConnection> WorkerLazy { get; init; } = null!;

    /// <summary>The connection to the worker, started on first use.</summary>
    internal WorkerConnection WorkerConnection => WorkerLazy.Value;

    /// <summary>True once the shell host was needed (reading <see cref="ShellHost"/> would start it).</summary>
    internal bool IsShellHostStarted => _shellHost.IsValueCreated;

    /// <summary>True once media information was needed (reading <see cref="MediaHost"/> would start the process).</summary>
    internal bool IsMediaHostStarted => _mediaHost.IsValueCreated;

    private static ProcessEntry ProcessEntryFor(string name, Leviathan.Engine.Supervision.SupervisedState state, int? pid, int restarts, string? disabledReason, string purpose)
    {
        var (label, detail, canReenable) = Leviathan.Engine.Supervision.HelperProcess.Describe(state, disabledReason, purpose);
        return new ProcessEntry(name, label, pid, restarts, detail, canReenable);
    }

    private WorkerJobControl? _jobControl;

    /// <summary>The Operations tab's pause, cancel, remove, move and priority actions on the worker's jobs.</summary>
    internal OperationActions JobActions => new(_jobControl ??= new WorkerJobControl(() => WorkerLazy.IsValueCreated ? WorkerLazy.Value : null), Elevation, ElevatedRunner);

    /// <summary>What queued copies, moves and deletes were asked to do, so a refused item can be done again as administrator (spec §15.6).</summary>
    internal Leviathan.ViewModels.Elevation.ElevationMemory Elevation { get; private init; } = new();

    private Leviathan.ViewModels.Elevation.IElevatedRunner? _elevatedRunner;

    /// <summary>Runs one request in the administrator helper after the Windows prompt (spec §15.6).</summary>
    internal Leviathan.ViewModels.Elevation.IElevatedRunner ElevatedRunner => _elevatedRunner ??= new ElevatedRunnerAdapter(
        new Leviathan.Interop.ElevatedHelper(new Leviathan.Interop.ProcessStarter(), Path.Combine(AppContext.BaseDirectory, "Leviathan.Elevated.exe")));

    /// <summary>Pauses a background job.</summary>
    public Task PauseJobAsync(Guid id) => WorkerLazy.IsValueCreated ? WorkerLazy.Value.PauseAsync(id) : Task.CompletedTask;

    /// <summary>Resumes a paused background job.</summary>
    public Task ResumeJobAsync(Guid id) => WorkerLazy.IsValueCreated ? WorkerLazy.Value.ResumeAsync(id) : Task.CompletedTask;

    /// <summary>Changes the priority of a background job.</summary>
    public Task SetJobPriorityAsync(Guid id, Leviathan.Contracts.JobPriority priority) =>
        WorkerLazy.IsValueCreated ? WorkerLazy.Value.SetPriorityAsync(id, priority) : Task.CompletedTask;

    /// <summary>Switches a process that crashed too often back on.</summary>
    public void ReenableProcess(string name)
    {
        if (name == "Worker" && WorkerLazy.IsValueCreated)
        {
            WorkerLazy.Value.Reenable();
        }
        else if (name == "Shell host" && IsShellHostStarted)
        {
            ShellHost.Reenable();
        }
        else if (name == "Media process" && IsMediaHostStarted)
        {
            MediaHost.Reenable();
        }
    }

    /// <summary>What the user did this session and, for seven days, earlier (spec 16.1, D35).</summary>
    public ActivityLog Activity { get; private init; } = null!;

    /// <summary>Asks the release feed whether a newer version exists (release plan §4); never applies anything.</summary>
    internal Leviathan.ViewModels.Updates.UpdateChecker Updates { get; private init; } = null!;

    /// <summary>The user's operations (copies, moves, deletes) as the Operations tab of the status hub lists them (spec 16.1).</summary>
    public OperationsModel Operations { get; private init; } = null!;

    private JsonFileStore<HubLayout> HubStore { get; init; } = null!;

    /// <summary>Every triage decision, written as it is made, so a crash loses none and kept files waiting for a move are offered again (spec §10.1, §3.4).</summary>
    public Leviathan.ViewModels.Triage.TriageJournal TriageJournal { get; private init; } = null!;

    /// <summary>Every triage decision kept for good, with its reject reason: the examples the recommendations learn from.</summary>
    public Leviathan.ViewModels.Triage.Learning.ITriageHistory TriageHistory { get; private init; } = null!;

    /// <summary>The user's own reasons for discarding a video; the list starts empty.</summary>
    public Leviathan.ViewModels.Triage.Learning.RejectReasonStore RejectReasons { get; private init; } = null!;

    /// <summary>The saved triage inboxes (spec §10.1).</summary>
    public InboxCatalog Inboxes { get; private init; } = null!;

    /// <summary>Finds the provider for a location, for work outside a tab (an inbox read).</summary>
    public LocationProviderRegistry Providers { get; private init; } = null!;

    private HubLayout _hubLayout = new();
    private Timer? _hubSaveTimer;

    /// <summary>The status hub's remembered tab, placement and size; read at startup.</summary>
    public HubLayout HubLayout => Volatile.Read(ref _hubLayout);

    /// <summary>Remembers the status hub's layout; the file is written a moment after the last change (a drag changes it many times).</summary>
    public void SaveHubLayout(HubLayout layout)
    {
        Volatile.Write(ref _hubLayout, layout);
        var timer = _hubSaveTimer ??= new Timer(_ => _ = SaveHubLayoutNowAsync());
        timer.Change(TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
    }

    private async Task SaveHubLayoutNowAsync()
    {
        try
        {
            await HubStore.SaveAsync(HubLayout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The layout is only a convenience; the next change tries again.
        }
    }

    /// <summary>Draws the app's own icons from the chosen icon pack (spec §4.3).</summary>
    internal IconService Icons { get; private init; } = null!;

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
        Volatile.Write(ref _hubLayout, await HubStore.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new HubLayout());
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
        await RefreshCloudStatesAsync();
        CloudFoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How each cloud sync client is doing (spec 7 Internet): running, not running, or its folder gone.</summary>
    public IReadOnlyDictionary<CloudProviderInfo, CloudSyncState> CloudStates => Volatile.Read(ref _cloudStates);

    /// <summary>Looks at the running programs again and tells the windows when a client started or stopped.</summary>
    public async Task RefreshCloudStatesAsync()
    {
        var providers = CloudProviders;
        if (providers.Count == 0)
        {
            return;
        }

        var states = await Task.Run(() => _cloudProbe.StatesOf(providers));
        var before = Volatile.Read(ref _cloudStates);
        if (before.Count == states.Count && states.All(s => before.TryGetValue(s.Key, out var old) && old == s.Value))
        {
            return;
        }

        Volatile.Write(ref _cloudStates, states);
        CloudFoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a new tab opens. The user profile folder; resolved from the environment, so no disk access at startup.</summary>
    public LocationPath Home { get; }

    /// <summary>Health of every network server seen, for the tree, tab banners and status bar (spec 3.3).</summary>
    public ShareHealthRegistry ShareHealth { get; init; } = null!;

    /// <summary>The servers listed under Network in the tree: added by Add connection and by opening a share (spec 7.1).</summary>
    public SavedConnectionList SavedServers { get; init; } = null!;

    /// <summary>Negotiated link speed to servers, for the status bar (spec 3.5).</summary>
    public LinkSpeedProbe LinkSpeed { get; } = new(new DnsHostAddressResolver(), new SystemAdapterTable());

    /// <summary>Devices found on the network by WS-Discovery and mDNS, for Network ▸ Nearby; searching runs in the background (spec 7.1).</summary>
    public NearbyService Nearby { get; init; } = null!;

    /// <summary>The servers the user chose to "Work offline" with: their folders come from the offline cache without touching the network (spec 3.3).</summary>
    public WorkOfflineRegistry WorkOffline { get; init; } = null!;

    /// <summary>Reconnects mapped drives and signs in to shares through Windows' own dialogs and Credential Manager (spec 3.3).</summary>
    public IShareConnector Connector { get; init; } = null!;

    public static AppHost Create(IUiContext ui)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var home = LocationPath.TryParse(profile, out var parsed) ? parsed : LocationPath.Parse(@"C:\");
        UiTrace.Mark("AppHost.Create begins");
        // Every network listing feeds the server's health and an unreachable server fails fast (spec 3.3).
        var identities = new ServerIdentityMap(new DnsHostAddressResolver());
        var shareHealth = new ShareHealthRegistry(identities);
        var workOffline = new WorkOfflineRegistry(identities);
        // A disconnected mapped drive is reconnected with the credentials Windows holds the first time it is used (spec 3.3).
        var connector = new WindowsShareConnector();
        // A server's own root (\\nas) lists its shares, so servers open and expand like drives (spec 7.1).
        // The index behind the colour tags is opened on first use; the tag folders (shell:tag\N) list from it.
        var tagIndex = new Lazy<Leviathan.ViewModels.Tags.ITagIndex>(() => new SqliteTagIndex(new LeviathanDatabase(LeviathanDatabase.DefaultPath)));
        // Search results are a folder too (shell:search\...): listed by walking the folders, with tags and media information from the app's own stores (spec 13).
        var searchHooks = new SearchHooks();
        // SFTP, FTP and FTPS servers (spec 7.1): passwords in Credential Manager, trusted keys in known-hosts.json, new servers confirmed in a dialog (TrustPrompt, set by the first window).
        var remoteSecrets = new Leviathan.Interop.WindowsRemoteSecrets();
        var knownHosts = new Leviathan.Providers.Remote.KnownHostsFile();
        var trustPrompt = new TrustPrompt();
        var remoteProvider = new Leviathan.Providers.Remote.RemoteLocationProvider(new Dictionary<Leviathan.Providers.Remote.RemoteProtocol, Leviathan.Providers.Remote.IRemoteSessionFactory>
        {
            [Leviathan.Providers.Remote.RemoteProtocol.Sftp] = new Leviathan.Providers.Remote.SftpSessionFactory(remoteSecrets, knownHosts, trustPrompt.AskAsync),
            [Leviathan.Providers.Remote.RemoteProtocol.Ftp] = new Leviathan.Providers.Remote.FtpSessionFactory(remoteSecrets, knownHosts, trustPrompt.AskAsync),
            [Leviathan.Providers.Remote.RemoteProtocol.Ftps] = new Leviathan.Providers.Remote.FtpSessionFactory(remoteSecrets, knownHosts, trustPrompt.AskAsync),
        });
        var busyDevices = new Leviathan.ViewModels.Searches.BusyDevices();
        LocationProviderRegistry? searchProviders = null;
        // Archives open as folders (spec 15.4): the local provider is wrapped so a path below a zip, 7z, rar, tar or iso is listed from the
        // archive. Unpacked copies for opening and previewing go to a scratch folder in the temp folder, swept of old files at start.
        var archiveDisk = new FileSystem();
        var archiveIndex = new Leviathan.Providers.Archive.ArchiveIndex(Leviathan.Providers.Archive.CompositeArchiveReader.Default(), archiveDisk);
        var archiveStager = new Leviathan.Providers.Archive.ArchiveStager(archiveIndex, archiveDisk, Path.Combine(Path.GetTempPath(), "Leviathan", "archive-stage"));
        var providers = new LocationProviderRegistry([
            new HealthTrackingLocationProvider(
                new ReconnectingLocationProvider(new Leviathan.Providers.Archive.ArchiveAwareLocationProvider(new LocalLocationProvider(), archiveIndex), connector, MappedDrives.IsMappedDrive), shareHealth, offline: workOffline),
            new HealthTrackingLocationProvider(new ServerSharesProvider(new WindowsShareEnumerator()), shareHealth, offline: workOffline),
            new Leviathan.ViewModels.Tags.TagListingProvider(tagIndex, TagFileStat.Describe),
            remoteProvider,
            new Leviathan.Search.SearchListingProvider(options => searchHooks.Coordinator(searchProviders!, options), () => searchHooks.Facts(), TimeProvider.System, report => searchHooks.Reported?.Invoke(report))]);
        searchProviders = providers;
        UiTrace.Mark("AppHost: providers");
        var recent = new RecentLocations();
        var lister = new ProviderFolderLister(providers);
        var queue = DispatcherQueue.GetForCurrentThread();
        // The operations list of the status hub (spec 16.1) hears every job the worker reports; the worker itself is still only
        // started when something needs it.
        var operations = new OperationsModel();
        AppHost? built = null;
        var updates = NewUpdates(() => built!);
        UiTrace.Mark("AppHost: operations model");
        // Video thumbnails (spec §10.4): the shell's own when it has one, else a frame ffmpeg draws in the media process. The gate stays
        // outermost, so "no thumbnails on network drives" also keeps ffmpeg off them. LEVIATHAN_VIDEO_FRAMES=1 skips the shell for video.
        var thumbnailGate = new NetworkThumbnailGate(new VideoThumbnailSource(
            new ShellThumbnailSource(),
            new FfmpegFrameSource(() => built!.MediaHost),
            preferFrames: Environment.GetEnvironmentVariable("LEVIATHAN_VIDEO_FRAMES") == "1"));
        UiTrace.Mark("AppHost: thumbnail gate");
        var thumbnails = new ThumbnailCache(thumbnailGate);
        BackgroundModel? background = null;
        var worker = new Lazy<WorkerConnection>(() =>
        {
            var connection = new WorkerConnection(new DispatcherQueueUiDispatcher(queue), []);
            // Whatever the user chose for how much runs at once reaches the worker as soon as it is connected, and again on every change.
            connection.StatusChanged += (_, status) =>
            {
                if (status.Connected && built is not null)
                {
                    _ = built.ApplyLimitsAsync(connection);
                }
            };
            connection.JobChanged += (_, job) =>
            {
                operations.Apply(job);
                background?.Apply(job);
                busyDevices.Apply(job);
            };
            return connection;
        });
        background = new BackgroundModel(
            () => thumbnails.Stats,
            [
                () => worker.IsValueCreated
                    ? ProcessEntryFor("Worker", worker.Value.State, worker.Value.ProcessId, worker.Value.RestartCount, worker.Value.DisabledReason, "Copies, moves, deletes and folder sizes")
                    : new ProcessEntry("Worker", "Not started", null, 0, "Starts when the first copy, delete or folder size needs it"),
                () => built is { IsShellHostStarted: true } host
                    ? ProcessEntryFor("Shell host", host.ShellHost.State, host.ShellHost.SupervisedProcessId, host.ShellHost.RestartCount, host.ShellHost.DisabledReason, "Right-click menus of other programs, kept out of this window")
                    : new ProcessEntry("Shell host", "Not started", null, 0, "Starts at the first right-click menu"),
                () => built is { IsMediaHostStarted: true } host
                    ? ProcessEntryFor("Media process", host.MediaHost.State, host.MediaHost.SupervisedProcessId, host.MediaHost.RestartCount, host.MediaHost.DisabledReason, "Reads video and audio details, kept out of this window")
                    : new ProcessEntry("Media process", "Not started", null, 0, "Starts when a folder with video or audio is shown"),
            ]);
        var mediaBackground = new MediaBackground(() => built is { IsMediaHostStarted: true } host ? host.MediaHost : null, () => built!.MediaHost);
        background.Media = () => mediaBackground.Latest;
        background.RefreshMedia = mediaBackground.Refresh;
        background.RetryMediaAsync = mediaBackground.RetryAsync;
        var sheetClient = new ContactSheetClient(() => built!.MediaHost);
        var sheetBatch = new ContactSheetBatch(sheetClient, background.NotifyChanged);
        background.ContactSheets = () => sheetBatch.Stats;
        background.CancelContactSheets = sheetBatch.Cancel;
        var sizes = new WorkerFolderSizes(() => worker.Value);
        var viewOptions = new ViewOptions();
        var viewRules = new FolderViewRules();
        // LEVIATHAN_DATA_DIR moves settings, Last session and profiles elsewhere: a portable copy, or a test run that must not
        // touch the real ones.
        var data = DataFolder();
        UiTrace.Mark("AppHost: data folder");

        // Folder listings are kept between runs in their own file (nothing is opened until the first folder is shown).
        var listings = new ListingCache(store: new SqliteListingStore(Path.Combine(data, "listings.db")));
        UiTrace.Mark("AppHost: listing cache");
        // The folders chosen for the search index (spec §13) are listed in the background, one at a time, never while a job copies to the same share, and saved in SQLite
        // (opened only when a folder was chosen). A folder you open also refreshes its entries in the index.
        var indexManager = new Leviathan.Search.IndexManager(
            searchHooks.Index,
            providers,
            new LazyNameIndexStore(() => new SqliteNameIndexStore(new LeviathanDatabase(LeviathanDatabase.DefaultPath))),
            new JsonIndexedFolders(Path.Combine(data, "indexed-folders.json")),
            TimeProvider.System,
            isBusy: busyDevices.IsBusy);
        listings.Listed += (key, entries) =>
        {
            if (indexManager.IsIndexed(key.Location))
            {
                _ = Task.Run(() => indexManager.OnFolderListed(key.Location, entries, key.IncludeHidden && key.IncludeSystem));
            }
        };
        background.SearchIndex = () => indexManager.Status();
        background.SearchIndexPaused = () => indexManager.IsPaused;
        background.SetSearchIndexPaused = indexManager.SetPaused;
        indexManager.Changed += (_, _) => background.NotifyChanged();
        // Colour tags (spec §11.2): the stream on the file, plus the SQLite index (opened on first use) for lookups and for locations without streams.
        var tagStore = new Leviathan.ViewModels.Tags.IndexedTagStore(
            new AlternateStreamTagStore(),
            tagIndex,
            new Leviathan.Interop.WindowsFileIdentity());
        searchHooks.Facts = () => new Leviathan.ViewModels.Searches.AppSearchFacts(tagStore, () => built!.Settings.Current.Tags.Catalog(), new MediaInfoSource(() => built!.MediaHost));
        // Network folders: a cheap stat now and then, never while the server is unreachable or worked with offline (spec 3.3).
        var polling = new PollingFolderWatcher { CanPoll = folder => !workOffline.IsOffline(folder) && shareHealth.HealthOf(folder) != Leviathan.Core.Resilience.ShareHealth.Unreachable };
        background.Network = () => new NetworkStats(polling.Watching, listings.FolderCount, listings.EntryCount, listings.MaxEntries);
        var pending = new PendingChanges();
        var services = new TabServices(providers, ui, viewRules, TimeProvider.System, () => built?.NewTabLocation() ?? home, recent, sizes, viewOptions, listings, new CompositeFolderWatcher(
            new LocalFolderWatcher(),
            polling), new MediaInfoSource(() => built!.MediaHost), pending, tagStore, () => built!.Settings.Current.Tags.Catalog());
        UiTrace.Mark("AppHost: tab services");
        var disk = new FileSystem();
        var triageJournal = new Leviathan.ViewModels.Triage.TriageJournal(disk, Path.Combine(Leviathan.Core.Platform.DataLocation.Root(), "triage-journal.jsonl"));
        NewItemService newItems;
        using (UiTrace.Begin("AppHost: new items"))
        {
            newItems = new NewItemService(new ShellNewCatalog(new WindowsRegistryView(), disk, NewMenuOptions.ForThisMachine()), new NewItemCreator(disk)) { Remote = new RemoteItemsAdapter(remoteProvider) };
        }
        var transfers = new WorkerTransfers(() => worker.Value);
        var elevation = new Leviathan.ViewModels.Elevation.ElevationMemory();
        transfers.Submitted += (_, s) => elevation.Remember(s.Job, s.Kind, [.. s.Sources.Select(p => p.Value)], s.Destination?.Value, permanent: false);

        // Saved sync pairs (spec 15.5) are checked once a minute for those due; a check only compares and records what is waiting, except an
        // update pair set to run on its own. The worker starts only when a pair is actually due.
        var syncPairs = new Leviathan.ViewModels.Sync.SyncPairStore(disk, Path.Combine(data, "sync-pairs.json"));
        var syncChecker = new Leviathan.ViewModels.Sync.SyncPairChecker(syncPairs, new WorkerCompare(() => worker.Value), new Leviathan.ViewModels.Sync.SyncRunner(transfers, transfers), TimeProvider.System);
        var undo = new Leviathan.ViewModels.Undo.UndoService(new JsonUndoStorage(), new WorkerUndoActions(() => worker.Value, () => transfers.KeepOwnTrashSource()));
        transfers.DeleteCompleted += (_, moves) => undo.RecordDelete(moves);
        var binService = new WorkerBinService(transfers, () => built?.Settings.Current.Files ?? new Leviathan.ViewModels.Settings.FilesSettings());

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
        ActivityLog activity;
        using (UiTrace.Begin("AppHost: activity log"))
        {
            activity = new ActivityLog(new SqliteActivityStore(Path.Combine(data, "activity.db")));
        }

        // How our own copies, moves and deletes ended belongs in the log whichever window asked (spec 16.1).
        transfers.Finished += (_, done) => activity.Note(
            done.Describe(),
            ActivityKind.Message,
            (done.Destination ?? (done.Sources.Count > 0 ? done.Sources[0].Parent : null))?.Value,
            done.Job.State != Leviathan.Contracts.JobState.Completed,
            done.Job.Id);
        var windows = new WindowManager(services);
        IconService icons;
        using (UiTrace.Begin("AppHost: icon packs"))
        {
            icons = new IconService(IconPacks.Discover(disk, Path.Combine(AppContext.BaseDirectory, "Assets", "Icons")), queue);
        }

        UiTrace.Mark("AppHost: windows");
        var sessionStore = new JsonFileStore<SessionFile>(disk, Path.Combine(data, "session.json"));
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(disk, Path.Combine(data, "profiles.json")));
        var recorder = new SessionRecorder(windows, sessionStore, ui);
        var multicast = new UdpMulticastTransport();
        UiTrace.Mark("AppHost: stores");
        var host = new AppHost(
            windows,
            new AddressResolver(new ArchiveAwareEntryProbe(new FileSystemEntryProbe(new FileSystem()), new Leviathan.Providers.Archive.ArchiveCatalog(archiveIndex))),
            new Leviathan.Core.Archives.StagingFileLauncher(new WindowsFileLauncher(), archiveStager),
            new WindowsKnownFolders(),
            new WindowsVolumeInfo(),
            UiTrace.Measure("AppHost: key map", WindowCommands.DefaultKeyMap),
            home,
            recent,
            new AddressCompleter(lister),
            new SubfolderMenu(lister),
            thumbnails,
            new FavoritesList(new JsonFavoritesStorage()),
            new ProviderSubfolderSource(providers),
            newItems,
            viewOptions,
            new RenameService(disk, new RemoteItemsAdapter(remoteProvider)))
        {
            ShareHealth = shareHealth,
            SavedServers = new SavedConnectionList(new JsonSavedConnectionsStorage(Path.Combine(data, "servers.json"))),
            Nearby = new NearbyService([new MdnsDiscovery(multicast), new WsDiscovery(multicast)]),
            WorkOffline = workOffline,
            Connector = connector,
            Transfers = transfers,
            Elevation = elevation,
            Archives = new WorkerArchives(transfers),
            SyncPairs = syncPairs,
            SyncChecker = syncChecker,
            Pending = pending,
            Undo = undo,
            TriageActionsFor = destination => new TriageFileActions(
                new WorkerUndoActions(() => worker.Value, () => transfers.KeepOwnTrashSource()),
                tagStore,
                () =>
                {
                    var media = built?.Settings.Current.Media;
                    return new TriageKeepOptions(destination() ?? media?.TriageKeepFolder, media?.TriageMoveWhen == Leviathan.ViewModels.Settings.TriageMoveMode.OnKeep);
                },
                transfers,
                () => built!.Settings.Current.Tags.Catalog()),
            Tagging = new Leviathan.ViewModels.Tags.FileTagging(tagStore, () => built!.Settings.Current.Tags.Catalog()),
            TagStore = tagStore,
            SearchHooks = searchHooks,
            RemoteConnections = new Leviathan.ViewModels.Sidebar.RemoteConnectionList(new JsonRemoteConnectionsStorage(Path.Combine(data, "remote-connections.json"))),
            RemoteSecrets = remoteSecrets,
            RemoteProvider = remoteProvider,
            Trust = trustPrompt,
            ArchiveIndex = archiveIndex,
            ArchiveStager = archiveStager,
            SearchIndex = indexManager,
            SpaceScanner = new Leviathan.SpaceMap.SpaceScanner(providers, new LazySpaceScanStore(() => new SqliteSpaceScanStore(new LeviathanDatabase(LeviathanDatabase.DefaultPath)))),
            Listings = listings,
            Icons = icons,
            Operations = operations,
            Activity = activity,
            Background = background,
            Bin = new BinModel(binService),
            Updates = updates.Checker,
            UpdateInstaller = updates.Installer,
            Sizes = sizes,
            Git = new Leviathan.ViewModels.Git.GitStatusService(new Leviathan.Interop.GitProcessRunner(), disk, () => built!.Settings.Current.Files.GitStatus),
            ThumbnailGate = thumbnailGate,
            SheetClient = sheetClient,
            Scrub = new ScrubStripCache(new ScrubStripClient(() => built!.MediaHost)),
            SheetBatch = sheetBatch,
            WorkerLazy = worker,
            BinService = binService,
            HubStore = new JsonFileStore<HubLayout>(disk, Path.Combine(data, "hub.json")),
            TriageJournal = triageJournal,
            TriageHistory = new Leviathan.Data.SqliteTriageHistory(Path.Combine(data, "triage-history.db")),
            RejectReasons = new Leviathan.ViewModels.Triage.Learning.RejectReasonStore(disk, Path.Combine(data, "reject-reasons.json")),
            Inboxes = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Path.Combine(data, "inboxes.json")), journal: triageJournal),
            Providers = providers,
            Settings = new SettingsStore(disk, Path.Combine(data, "settings.json")),
            SettingsPath = Path.Combine(data, "settings.json"),
            ViewRulesStore = new ViewRulesPersistence(viewRules, new JsonFileStore<ViewRulesFile>(disk, Path.Combine(data, "viewrules.json"))),
            SessionStore = sessionStore,
            Recorder = recorder,
            Profiles = new ProfileService(windows, catalog, recorder),
        };

        // The icon pack is a setting that applies without a restart (spec §17).
        built = host;
        using (UiTrace.Begin("AppHost: apply settings"))
        {
            host.StartApplyingSettings(ui);
        }

        host.StartUpdateChecks();
        transfers.VerifySource = () => host.Settings.Current.Performance.Verify;
        transfers.Volumes = host.Volumes;
        transfers.KeepOwnTrashSource = () => host.Settings.Current.Files.KeepOwnTrashOnShares;

        return host;
    }
}

