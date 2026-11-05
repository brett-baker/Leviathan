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
/// <param name="Options">Whether hidden and system items are listed; null lists neither.</param>
/// <param name="Cache">Remembered folder listings shown at once and checked in the background; null lists every folder afresh.</param>
/// <param name="Watcher">Reports changes to the folder on screen so its rows follow the disk; null leaves them as listed.</param>
public sealed record TabServices(LocationProviderRegistry Providers, IUiContext Ui, FolderViewRules ViewRules, TimeProvider Time, Func<LocationPath> Home, RecentLocations? Recent = null, IFolderSizeSource? FolderSizes = null, ViewOptions? Options = null, ListingCache? Cache = null, IFolderWatcher? Watcher = null);

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
public sealed record TabState(
    NavigationSnapshot History,
    FolderViewSettings View,
    string? CustomTitle,
    string? Accent,
    bool IsPinned,
    bool IsLocked,
    bool IsSplit,
    IReadOnlyList<LocationPath> Selection,
    Guid? GroupId = null);

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

    [ObservableProperty]
    private bool _isSplit;

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
        Folder = new FolderViewModel(services.Providers, services.Ui, services.Time, services.Cache, services.Watcher);
        Items = new FolderItemsView(Folder.Entries, time: services.Time);
        Selection = new SelectionModel(Items.Rows, services.Time);
        Address = new AddressBarViewModel(history.Current);
        _explicitView = explicitView ?? FolderViewSettings.Default;
        Columns = new ColumnLayout(_explicitView.Columns, _explicitView.ColumnWidths);
        Sizes = new FolderSizeTracker(services.FolderSizes, services.Ui);
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
                if (Folder.State is FolderLoadState.Loaded)
                {
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
        GroupId);

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
            IsSplit = state.IsSplit,
            GroupId = state.GroupId,
        };
        tab._pendingSelection = state.Selection;
        return tab;
    }

    private IReadOnlyList<LocationPath>? _pendingSelection;

    /// <inheritdoc />
    public void Dispose()
    {
        _services.ViewRules.Changed -= OnRulesChanged;
        Sizes.Dispose();
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
