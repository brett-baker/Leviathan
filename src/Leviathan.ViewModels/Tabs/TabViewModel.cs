// View models are UI-thread affine: everything a control can observe is changed on the thread that started the command, so the
// awaits in this file deliberately resume on the caller's context (crash signature 52a0ec04c34d: RPC_E_WRONG_THREAD from a
// continuation that ran on the thread pool). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tabs;

/// <summary>What every tab needs from the rest of the app.</summary>
/// <param name="Providers">Location providers.</param>
/// <param name="Ui">Runs work on the UI thread.</param>
/// <param name="ViewRules">Saved folder views with inheritance (spec §8.1).</param>
/// <param name="Time">Clock.</param>
/// <param name="Home">The location a new tab opens (spec §4.1).</param>
/// <param name="Recent">Folders visited recently, shared by all tabs; every navigation is recorded there.</param>
/// <param name="FolderSizes">Where folder totals for the Size column come from; null shows no folder sizes.</param>
/// <param name="MediaInfo">Where duration, resolution and the other media columns come from (the media process); null leaves them empty.</param>
/// <param name="Options">Whether hidden and system items are listed; null lists neither.</param>
/// <param name="Cache">Remembered folder listings shown at once and checked in the background; null lists every folder afresh.</param>
/// <param name="Watcher">Reports changes to the folder on screen so its rows follow the disk; null leaves them as listed.</param>
/// <param name="Pending">What running deletes, moves and copies are about to do to the rows (the "UI updates: Immediate" setting); null shows only what is on disk.</param>
public sealed record TabServices(LocationProviderRegistry Providers, IUiContext Ui, FolderViewRules ViewRules, TimeProvider Time, Func<LocationPath> Home, RecentLocations? Recent = null, IFolderSizeSource? FolderSizes = null, ViewOptions? Options = null, ListingCache? Cache = null, IFolderWatcher? Watcher = null, IMediaInfoSource? MediaInfo = null, PendingChanges? Pending = null);

/// <summary>A tab in a form that can be copied (duplicate) or saved (closed-tab stack, startup profiles) (spec §4.1, §17.1).</summary>
/// <param name="History">Back, current and forward locations.</param>
/// <param name="View">Layout, sort, grouping and columns in effect.</param>
/// <param name="CustomTitle">User-chosen label, or null.</param>
/// <param name="Accent">User-chosen accent colour (<c>#RRGGBB</c>), or null.</param>
/// <param name="IsPinned">Pinned to the left, icon only.</param>
/// <param name="IsLocked">Navigating opens a new tab instead.</param>
/// <param name="IsSplit">Two panes (Phase 2 fills in the second pane).</param>
/// <param name="Selection">Selected items.</param>
/// <param name="GroupId">The group the tab is in (spec §4.1), or null.</param>
/// <param name="Split">The second pane when the tab is split (spec §15.1), or null for one pane.</param>
public sealed record TabState(
    NavigationSnapshot History,
    FolderViewSettings View,
    string? CustomTitle,
    string? Accent,
    bool IsPinned,
    bool IsLocked,
    bool IsSplit,
    IReadOnlyList<LocationPath> Selection,
    Guid? GroupId = null,
    SplitState? Split = null);

/// <summary>How the panes of a split tab are laid out (spec §15.1, §6.3).</summary>
public enum PaneLayout
{
    /// <summary>One pane.</summary>
    Single,

    /// <summary>Two panes next to each other (Ctrl+Backslash).</summary>
    SideBySide,

    /// <summary>Two panes, one above the other (Ctrl+Shift+Backslash).</summary>
    TopBottom,
}

/// <summary>The second pane of a split tab, saved with the tab, the session and profiles (spec §15.1).</summary>
/// <param name="Layout">Side by side or top and bottom.</param>
/// <param name="Secondary">The second pane's own path, history, view and selection.</param>
/// <param name="FocusedPane">0 for the first pane, 1 for the second.</param>
public sealed record SplitState(PaneLayout Layout, TabState Secondary, int FocusedPane);

/// <summary>
/// One tab: its history, folder listing, sorted view, selection, address bar and the tab-level options (pin, lock, label,
/// colour). The window's tab strip owns a list of these (spec §4.1).
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    private readonly TabServices _services;
    private FolderViewSettings _explicitView;
    private bool _useExplicitView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? _customTitle;

    [ObservableProperty]
    private string? _accent;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isLocked;

    /// <summary>The group this tab belongs to (spec §4.1), or null. Set by <see cref="TabStripViewModel"/>, which keeps the tabs of a group together.</summary>
    [ObservableProperty]
    private Guid? _groupId;

    [ObservableProperty]
    private ViewLayout _layout = ViewLayout.Details;

    [ObservableProperty]
    private int _thumbnailSize = ItemFlow.DefaultThumbnailSize;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private LocationPath? _renaming;

    /// <summary>Creates a tab at <paramref name="start"/>.</summary>
    public TabViewModel(TabServices services, LocationPath start)
        : this(services, new NavigationHistory(start), null)
    {
    }

    private TabViewModel(TabServices services, NavigationHistory history, FolderViewSettings? explicitView)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        History = history;
        Folder = new FolderViewModel(services.Providers, services.Ui, services.Time, services.Cache, services.Watcher, services.Pending);
        Items = new FolderItemsView(Folder.Entries, time: services.Time);
        Selection = new SelectionModel(Items.Rows, services.Time);
        Address = new AddressBarViewModel(history.Current);
        _explicitView = explicitView ?? FolderViewSettings.Default;
        Columns = new ColumnLayout(_explicitView.Columns, _explicitView.ColumnWidths);
        Sizes = new FolderSizeTracker(services.FolderSizes, services.Ui);
        Media = new MediaInfoTracker(services.MediaInfo, services.Ui, services.Time);

        // Sorting by a folder's size, item count or file count: the view asks the tracker, and sorts again when a number arrives.
        Items.SetFolderMetrics(Sizes.ValueOf);
        Sizes.Changed += (_, _) => Items.ResortByMetrics();

        // The same for the media columns: a duration that arrives can move a row, and sorting by one reads the whole folder (spec §8.2, §9.3).
        Items.SetMediaKeys(Media.SortKeyOf);
        Media.Changed += (_, _) => Items.ResortByMedia();
        Items.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderItemsView.Sort))
            {
                UpdateMediaFill();
            }
        };
        _useExplicitView = explicitView is not null;
        ApplyView();

        Folder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderViewModel.Kind))
            {
                ApplyView();
            }
            else if (e.PropertyName == nameof(FolderViewModel.OfflineListing))
            {
                OnPropertyChanged(nameof(IsOffline));
            }
            else if (e.PropertyName == nameof(FolderViewModel.State))
            {
                // A new listing starts: whatever media information was being read for the old folder is cancelled (spec §9.3).
                if (Folder.State is FolderLoadState.Loading)
                {
                    Media.SetFolder();
                }

                if (Folder.State is FolderLoadState.Loaded)
                {
                    UpdateMediaFill();
                    Sizes.Begin(Items.Rows);
                }

                OnPropertyChanged(nameof(IsOffline));
                OnPropertyChanged(nameof(IsBusy));
            }
        };
        services.ViewRules.Changed += OnRulesChanged;
    }

    /// <summary>Stable identity, for drag and drop between windows.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The second pane of a split tab, or null. It is a pane of this tab, not a tab of the strip: it has its own path, history, view and selection (spec §15.1).</summary>
    public TabViewModel? Secondary { get; private set; }

    /// <summary>How the panes are laid out; <see cref="PaneLayout.Single"/> when there is no second pane.</summary>
    public PaneLayout Panes { get; private set; }

    /// <summary>0 when the first pane has the focus, 1 for the second. Always 0 without a second pane.</summary>
    public int FocusedPane { get; private set; }

    /// <summary>True while the tab shows two panes. The bindings that mean "to the other pane" (F5, F6) only count then (spec §17.3).</summary>
    public bool IsSplit => Secondary is not null;

    /// <summary>The pane the address bar, command bar, status bar and preview follow: the second pane while it has the focus, else this tab.</summary>
    public TabViewModel Focused => FocusedPane == 1 && Secondary is not null ? Secondary : this;

    /// <summary>The pane that does not have the focus, or null without a split.</summary>
    public TabViewModel? Other => Secondary is null ? null : FocusedPane == 1 ? this : Secondary;

    /// <summary>
    /// Splits the tab (spec §15.1) or changes how an existing split is laid out. The new pane opens the folder the focused pane shows,
    /// with its view. <see cref="PaneLayout.Single"/> closes the split and keeps the focused pane.
    /// </summary>
    public async Task SetPanesAsync(PaneLayout layout, CancellationToken cancellationToken)
    {
        if (layout == PaneLayout.Single)
        {
            await CloseSplitAsync(cancellationToken);
            return;
        }

        if (Secondary is null)
        {
            var start = Focused;
            var pane = new TabViewModel(_services, new NavigationHistory(start.History.Current), start.CurrentView());
            pane._pendingSelection = [.. start.Selection.SelectedEntries.Select(e => e.Location)];
            AttachSecondary(pane);
            Panes = layout;
            RaisePanesChanged();
            await pane.ReloadAsync(cancellationToken);
            return;
        }

        if (Panes != layout)
        {
            Panes = layout;
            RaisePanesChanged();
        }
    }

    /// <summary>Gives the keyboard focus to the first (0) or second (1) pane; a tab without a split only has the first.</summary>
    public void FocusPane(int pane)
    {
        var wanted = pane == 1 && Secondary is not null ? 1 : 0;
        if (wanted != FocusedPane)
        {
            FocusedPane = wanted;
            RaisePanesChanged();
        }
    }

    /// <summary>Closes the split and keeps the pane that has the focus (spec §15.1): when that is the second, this tab goes to its folder with its view.</summary>
    public async Task CloseSplitAsync(CancellationToken cancellationToken)
    {
        if (Secondary is not { } second)
        {
            return;
        }

        var keepSecond = FocusedPane == 1;
        Secondary = null;
        Panes = PaneLayout.Single;
        FocusedPane = 0;
        second.PropertyChanged -= OnSecondaryChanged;
        if (keepSecond)
        {
            // Not through NavigateAsync: a locked tab would open the folder in a new tab instead of showing it here.
            var result = History.Navigate(second.History.Current);
            if (result is not null)
            {
                await ShowAsync(result, cancellationToken);
            }

            AdoptView(second);
        }

        second.Dispose();
        RaisePanesChanged();
    }

    private void AdoptView(TabViewModel other) => ApplyViewSettings(other.CurrentView());

    // Takes a view (layout, thumbnails, sort, grouping, folders, columns) and keeps it for this pane until it navigates to a folder with a saved one.
    private void ApplyViewSettings(FolderViewSettings view)
    {
        SetLayout(view.Layout);
        SetThumbnailSize(view.ThumbnailSize);
        Items.SetSort(view.Sort);
        SetGroupBy(view.GroupBy);
        SetFolderPlacement(view.Folders);
        Columns.SetColumns(view.Columns);
    }

    /// <summary>
    /// Swaps what the two panes show (spec §15.1, Ctrl+Shift+S): each takes the other's history, folder, view and selection. The panes
    /// stay where they are on screen and keep the focus they had; the folders are listed again from the cache, so it is quick.
    /// </summary>
    public async Task SwapPanesAsync(CancellationToken cancellationToken)
    {
        if (Secondary is not { } second)
        {
            return;
        }

        var (mine, theirs) = (History.Snapshot(), second.History.Snapshot());
        var (myView, theirView) = (CurrentView(), second.CurrentView());
        var (mySelection, theirSelection) = (SelectedLocations(), second.SelectedLocations());

        History.Load(theirs);
        second.History.Load(mine);
        ApplyViewSettings(theirView);
        second.ApplyViewSettings(myView);
        _pendingSelection = theirSelection;
        second._pendingSelection = mySelection;
        await Task.WhenAll(ShowCurrentAsync(cancellationToken), second.ShowCurrentAsync(cancellationToken));
    }

    private IReadOnlyList<LocationPath> SelectedLocations() => [.. Selection.SelectedEntries.Select(e => e.Location)];

    // Shows the folder of the history as it stands, without touching the history or the view that was just set.
    private async Task ShowCurrentAsync(CancellationToken cancellationToken)
    {
        Renaming = null;
        Address.Show(History.Current);
        OnPropertyChanged(nameof(Title));
        SetFilter(null);
        Folder.Options = ListingNow();
        await Folder.LoadAsync(History.Current, cancellationToken);
        SelectAfterLoad(null);
    }

    private void AttachSecondary(TabViewModel pane)
    {
        Secondary = pane;
        pane.PropertyChanged += OnSecondaryChanged;
    }

    // Anything the second pane does (it navigated) is something to save, like the same in the first.
    private void OnSecondaryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Secondary));

    private void RaisePanesChanged()
    {
        OnPropertyChanged(nameof(Secondary));
        OnPropertyChanged(nameof(Panes));
        OnPropertyChanged(nameof(FocusedPane));
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(Focused));
        OnPropertyChanged(nameof(Other));
    }

    /// <summary>Back, forward and up.</summary>
    public NavigationHistory History { get; }

    /// <summary>The listing of the current folder.</summary>
    public FolderViewModel Folder { get; }

    /// <summary>The filtered, sorted, grouped rows.</summary>
    public FolderItemsView Items { get; }

    /// <summary>Selection over <see cref="Items"/>.</summary>
    public SelectionModel Selection { get; }

    /// <summary>The Details columns: which, in what order, how wide. They stay with the tab when it navigates.</summary>
    public ColumnLayout Columns { get; }

    /// <summary>Folder totals for the Size column.</summary>
    public FolderSizeTracker Sizes { get; }

    /// <summary>Duration, resolution and the other media columns of the files shown, read lazily by the media process (spec §8.2, §9.3).</summary>
    public MediaInfoTracker Media { get; }

    /// <summary>True when a media column is among the visible columns (the media process is not asked for anything otherwise; Tiles also ask, spec §8.1).</summary>
    public bool ShowsMediaColumns => Columns.Slots.Any(s => s.Column.Media is not null);

    /// <summary>
    /// The rows on screen, reported by the grid once it has settled: reads their media information when a media column is shown, and
    /// does nothing when none is, so a folder of videos costs no process and no probe until the user asks for a column (spec §9.3).
    /// </summary>
    public void RequestMedia(IReadOnlyList<FileEntry> visibleRows)
    {
        ArgumentNullException.ThrowIfNull(visibleRows);
        if (ShowsMediaColumns || Layout == ViewLayout.Tiles)
        {
            Media.SetVisible(visibleRows);
        }
    }

    // Sorting by a media column needs every value, so the whole folder is read behind the rows on screen; any other sort stops that.
    private void UpdateMediaFill()
    {
        if (Items.SortsByMedia && Folder.State is FolderLoadState.Loaded)
        {
            Media.FillFolder(Folder.Entries);
        }
        else
        {
            Media.CancelFill();
        }
    }

    /// <summary>The breadcrumb address bar.</summary>
    public AddressBarViewModel Address { get; }

    /// <summary>The tab label: the custom label, else the folder name.</summary>
    public string Title => CustomTitle ?? History.Current.Name;

    /// <summary>True when the share is not responding: the strip shows a warning badge (spec §4.1).</summary>
    public bool IsOffline => Folder.State == FolderLoadState.Offline || Folder.OfflineListing is not null;

    /// <summary>True while a listing is in progress: the strip shows a thin progress line (spec §4.1).</summary>
    public bool IsBusy => Folder.State == FolderLoadState.Loading;

    /// <summary>Raised instead of navigating when the tab is locked; the window opens the location in a new tab.</summary>
    public event EventHandler<LocationPath>? OpenInNewTabRequested;

    /// <summary>
    /// Goes to <paramref name="location"/>. A locked tab asks for a new tab instead and stays where it is. Completes when
    /// the listing ends.
    /// </summary>
    public async Task NavigateAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (IsLocked && location != History.Current)
        {
            OpenInNewTabRequested?.Invoke(this, location);
            return;
        }

        var result = History.Navigate(location);
        if (result is not null)
        {
            await ShowAsync(result, cancellationToken);
        }
    }

    /// <summary>Back by <paramref name="steps"/> entries.</summary>
    public async Task GoBackAsync(CancellationToken cancellationToken, int steps = 1)
    {
        var result = History.GoBack(steps);
        if (result is not null)
        {
            await ShowAsync(result, cancellationToken);
        }
    }

    /// <summary>Forward by <paramref name="steps"/> entries.</summary>
    public async Task GoForwardAsync(CancellationToken cancellationToken, int steps = 1)
    {
        var result = History.GoForward(steps);
        if (result is not null)
        {
            await ShowAsync(result, cancellationToken);
        }
    }

    /// <summary>To the parent folder. A locked tab opens the parent in a new tab.</summary>
    public async Task GoUpAsync(CancellationToken cancellationToken)
    {
        if (History.Current.Parent is not { } parent)
        {
            return;
        }

        if (IsLocked)
        {
            OpenInNewTabRequested?.Invoke(this, parent);
            return;
        }

        var result = History.GoUp();
        if (result is not null)
        {
            await ShowAsync(result, cancellationToken);
        }
    }

    /// <summary>Lists the folder again, keeping the selection.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken, bool forceFresh = false)
    {
        // A refresh (F5) or the first listing of a restored tab keeps the selection.
        _pendingSelection ??= [.. Selection.SelectedEntries.Select(e => e.Location)];
        Folder.Options = ListingNow();
        await Folder.LoadAsync(History.Current, cancellationToken, forceFresh);
        SelectAfterLoad(null);
    }

    /// <summary>Sets the layout and remembers it for this tab until the next navigation resolves a saved view.</summary>
    public void SetLayout(ViewLayout layout)
    {
        // The rename box belongs to the view that showed it.
        Renaming = null;
        Layout = layout;
        KeepCurrentView();
    }

    /// <summary>Ctrl+mouse wheel in Thumbnails: makes the thumbnails bigger (positive) or smaller, between 64 and 512 px (spec §8.1).</summary>
    public void ZoomThumbnails(int notches)
    {
        ThumbnailSize = ItemFlow.Zoom(ThumbnailSize, notches);
        KeepCurrentView();
    }

    /// <summary>The thumbnail size slider in the status bar: sets the size directly, clamped to 64–512 px.</summary>
    public void SetThumbnailSize(int size)
    {
        ThumbnailSize = ItemFlow.ClampThumbnailSize(size);
        KeepCurrentView();
    }

    /// <summary>Header click: sorts this tab's view. The choice holds until the tab navigates to a folder with a saved view.</summary>
    public void SortBy(string columnId, bool additive = false)
    {
        Items.SortBy(columnId, additive);
        KeepCurrentView();
    }

    /// <summary>
    /// Sort menu: sorts by <paramref name="columnId"/> in <paramref name="direction"/>, or keeps the direction in use when null.
    /// Unlike a header click, choosing the column already in use does not flip it.
    /// </summary>
    public void SetSort(string columnId, SortDirection? direction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnId);
        var current = Items.Sort.Count > 0 ? Items.Sort[0].Direction : SortDirection.Ascending;
        Items.SetSort([new SortKey(columnId, direction ?? current)]);
        KeepCurrentView();
    }

    /// <summary>Reloads the folder and selects <paramref name="location"/> once it is listed, e.g. a New menu item that was just created.</summary>
    public async Task ReloadAndSelectAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        _pendingSelection = [location];
        Renaming = null;
        Folder.Options = ListingNow();

        // The caller just changed the folder (created, renamed or restored something): what the cache holds is out of date.
        await Folder.LoadAsync(History.Current, cancellationToken, forceFresh: true);
        SelectAfterLoad(null);
    }

    /// <summary>Groups this tab's view by a column, or ungroups with null.</summary>
    public void SetGroupBy(string? columnId)
    {
        Items.SetGroupBy(columnId);
        KeepCurrentView();
    }

    /// <summary>
    /// Filters the listing as the user types (spec §6.3); it is not a search. The filter belongs to the folder being shown, so
    /// navigating clears it.
    /// </summary>
    public void SetFilter(string? text)
    {
        FilterText = text ?? string.Empty;
        Items.SetFilter(text);
    }

    /// <summary>
    /// The item the user means by "this one" for rename: the only selected item, or the focused item when it is selected. Null
    /// when the selection is empty or has several items.
    /// </summary>
    public FileEntry? RenameTarget()
    {
        var selected = Selection.SelectedEntries;
        return selected.Count == 1 ? selected[0] : null;
    }

    /// <summary>F2 and New: shows the rename box on <paramref name="entry"/> (the view scrolls to it and focuses the box).</summary>
    public void BeginRename(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Selection.Click(entry);
        Renaming = null;
        Renaming = entry.Location;
    }

    /// <summary>Hides the rename box without renaming.</summary>
    public void CancelRename() => Renaming = null;

    /// <summary>Sets where folders sort in this tab.</summary>
    public void SetFolderPlacement(FolderPlacement placement)
    {
        Items.SetFolderPlacement(placement);
        KeepCurrentView();
    }

    private void KeepCurrentView()
    {
        _explicitView = CurrentView();
        _useExplicitView = true;
    }

    /// <summary>
    /// Applies the column set chosen in the column chooser (spec §8.2) to this tab and, with <paramref name="forAllOfType"/>, saves the
    /// view as the rule for every folder of the kind this one is (spec §8.1: "Apply to all folders of this type").
    /// </summary>
    public void ApplyColumns(IReadOnlyList<string> ids, bool forAllOfType)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Columns.SetColumns(ids);
        if (forAllOfType)
        {
            _services.ViewRules.SetForKind(Folder.Kind, CurrentView());
        }
    }

    /// <summary>The view in effect (layout, sort, grouping, columns), e.g. to save as a folder rule.</summary>
    public FolderViewSettings CurrentView() => new()
    {
        Layout = Layout,
        Sort = Items.Sort,
        GroupBy = Items.GroupBy,
        Folders = Items.Folders,
        Columns = Columns.Ids,
        ColumnWidths = Columns.Widths,
        ThumbnailSize = ThumbnailSize,
    };

    /// <summary>Captures the tab for duplicating or saving.</summary>
    public TabState CaptureState() => new(
        History.Snapshot(),
        CurrentView(),
        CustomTitle,
        Accent,
        IsPinned,
        IsLocked,
        IsSplit,
        [.. Selection.SelectedEntries.Select(e => e.Location)],
        GroupId,
        Secondary is { } second ? new SplitState(Panes, second.CaptureState(), FocusedPane) : null);

    /// <summary>
    /// Rebuilds a tab from <paramref name="state"/>. The folder is not listed until <see cref="ReloadAsync"/> is called, so
    /// restoring a session never waits on a slow or dead share (spec §17.1).
    /// </summary>
    public static TabViewModel Restore(TabState state, TabServices services)
    {
        ArgumentNullException.ThrowIfNull(state);
        var tab = new TabViewModel(services, NavigationHistory.FromSnapshot(state.History), state.View)
        {
            CustomTitle = state.CustomTitle,
            Accent = state.Accent,
            IsPinned = state.IsPinned,
            IsLocked = state.IsLocked,
            GroupId = state.GroupId,
        };
        tab._pendingSelection = state.Selection;
        if (state.Split is { Layout: not PaneLayout.Single } split)
        {
            tab.AttachSecondary(Restore(split.Secondary with { Split = null }, services));
            tab.Panes = split.Layout;
            tab.FocusedPane = split.FocusedPane == 1 ? 1 : 0;
        }

        return tab;
    }

    private IReadOnlyList<LocationPath>? _pendingSelection;

    /// <inheritdoc />
    public void Dispose()
    {
        _services.ViewRules.Changed -= OnRulesChanged;
        Secondary?.Dispose();
        Sizes.Dispose();
        Media.Dispose();
        Folder.Dispose();
    }

    private async Task ShowAsync(NavigationResult result, CancellationToken cancellationToken)
    {
        // A navigation resolves the saved view for the new folder; a view set explicitly on this tab only lasts until then.
        _useExplicitView = false;
        Renaming = null;
        Address.Show(result.Location);
        _services.Recent?.Add(result.Location);
        OnPropertyChanged(nameof(Title));
        ApplyView();
        SetFilter(null);
        Folder.Options = ListingNow();
        await Folder.LoadAsync(result.Location, cancellationToken);
        SelectAfterLoad(result.SelectOnArrival);
    }

    private ListingOptions ListingNow() => _services.Options?.Listing ?? ListingOptions.Default;

    private void SelectAfterLoad(LocationPath? preferred)
    {
        var wanted = _pendingSelection;
        _pendingSelection = null;
        if (wanted is { Count: > 0 })
        {
            var rows = Items.Rows.Where(r => wanted.Contains(r.Location)).ToList();
            if (rows.Count > 0)
            {
                Selection.Click(rows[0]);
                foreach (var row in rows.Skip(1))
                {
                    Selection.Click(row, ctrl: true);
                }

                return;
            }
        }

        // After Back or Up the folder you came from is selected (spec §5.1). The view scrolls it into view.
        if (preferred is not null && Items.Rows.FirstOrDefault(r => r.Location == preferred) is { } entry)
        {
            Selection.Click(entry);
        }
    }

    private void OnRulesChanged(object? sender, EventArgs e) => ApplyView();

    private void ApplyView()
    {
        var settings = _useExplicitView ? _explicitView : _services.ViewRules.Resolve(History.Current, Folder.Kind);
        if (!_useExplicitView)
        {
            _explicitView = settings;
        }

        Layout = settings.Layout;
        ThumbnailSize = ItemFlow.ClampThumbnailSize(settings.ThumbnailSize);
        Items.Apply(settings);

        // A saved view brings its columns and widths; with none saved the tab keeps the columns the user set on it.
        if (!_useExplicitView && _services.ViewRules.TryResolve(History.Current, Folder.Kind, out _))
        {
            Columns.SetColumns(settings.Columns);
            foreach (var (id, width) in settings.ColumnWidths)
            {
                if (Columns.IsVisible(id))
                {
                    Columns.Resize(id, width);
                }
            }
        }
    }
}
