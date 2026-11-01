// View models are UI-thread affine: everything a control can observe is changed on the thread that started the command, so the
// awaits in this file deliberately resume on the caller's context (crash signature 52a0ec04c34d: RPC_E_WRONG_THREAD from a
// continuation that ran on the thread pool). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using CommunityToolkit.Mvvm.ComponentModel;
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
public sealed record TabServices(LocationProviderRegistry Providers, IUiContext Ui, FolderViewRules ViewRules, TimeProvider Time, Func<LocationPath> Home, RecentLocations? Recent = null);

/// <summary>A tab in a form that can be copied (duplicate) or saved (closed-tab stack, startup profiles) (spec §4.1, §17.1).</summary>
/// <param name="History">Back, current and forward locations.</param>
/// <param name="View">Layout, sort, grouping and columns in effect.</param>
/// <param name="CustomTitle">User-chosen label, or null.</param>
/// <param name="Accent">User-chosen accent colour (<c>#RRGGBB</c>), or null.</param>
/// <param name="IsPinned">Pinned to the left, icon only.</param>
/// <param name="IsLocked">Navigating opens a new tab instead.</param>
/// <param name="IsSplit">Two panes (Phase 2 fills in the second pane).</param>
/// <param name="Selection">Selected items.</param>
public sealed record TabState(
    NavigationSnapshot History,
    FolderViewSettings View,
    string? CustomTitle,
    string? Accent,
    bool IsPinned,
    bool IsLocked,
    bool IsSplit,
    IReadOnlyList<LocationPath> Selection);

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

    [ObservableProperty]
    private ViewLayout _layout = ViewLayout.Details;

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
        Folder = new FolderViewModel(services.Providers, services.Ui, services.Time);
        Items = new FolderItemsView(Folder.Entries, time: services.Time);
        Selection = new SelectionModel(Items.Rows, services.Time);
        Address = new AddressBarViewModel(history.Current);
        _explicitView = explicitView ?? FolderViewSettings.Default;
        _useExplicitView = explicitView is not null;
        ApplyView();

        Folder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderViewModel.Kind))
            {
                ApplyView();
            }
            else if (e.PropertyName == nameof(FolderViewModel.State))
            {
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

    /// <summary>The breadcrumb address bar.</summary>
    public AddressBarViewModel Address { get; }

    /// <summary>The tab label: the custom label, else the folder name.</summary>
    public string Title => CustomTitle ?? History.Current.Name;

    /// <summary>True when the share is not responding: the strip shows a warning badge (spec §4.1).</summary>
    public bool IsOffline => Folder.State == FolderLoadState.Offline;

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
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        // A refresh (F5) or the first listing of a restored tab keeps the selection.
        _pendingSelection ??= [.. Selection.SelectedEntries.Select(e => e.Location)];
        await Folder.LoadAsync(History.Current, cancellationToken);
        SelectAfterLoad(null);
    }

    /// <summary>Sets the layout and remembers it for this tab until the next navigation resolves a saved view.</summary>
    public void SetLayout(ViewLayout layout)
    {
        Layout = layout;
        KeepCurrentView();
    }

    /// <summary>Header click: sorts this tab's view. The choice holds until the tab navigates to a folder with a saved view.</summary>
    public void SortBy(string columnId, bool additive = false)
    {
        Items.SortBy(columnId, additive);
        KeepCurrentView();
    }

    /// <summary>Groups this tab's view by a column, or ungroups with null.</summary>
    public void SetGroupBy(string? columnId)
    {
        Items.SetGroupBy(columnId);
        KeepCurrentView();
    }

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

    /// <summary>The view in effect (layout, sort, grouping, columns), e.g. to save as a folder rule.</summary>
    public FolderViewSettings CurrentView() => new()
    {
        Layout = Layout,
        Sort = Items.Sort,
        GroupBy = Items.GroupBy,
        Folders = Items.Folders,
        Columns = _explicitView.Columns,
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
        [.. Selection.SelectedEntries.Select(e => e.Location)]);

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
        };
        tab._pendingSelection = state.Selection;
        return tab;
    }

    private IReadOnlyList<LocationPath>? _pendingSelection;

    /// <inheritdoc />
    public void Dispose()
    {
        _services.ViewRules.Changed -= OnRulesChanged;
        Folder.Dispose();
    }

    private async Task ShowAsync(NavigationResult result, CancellationToken cancellationToken)
    {
        // A navigation resolves the saved view for the new folder; a view set explicitly on this tab only lasts until then.
        _useExplicitView = false;
        Address.Show(result.Location);
        _services.Recent?.Add(result.Location);
        OnPropertyChanged(nameof(Title));
        ApplyView();
        await Folder.LoadAsync(result.Location, cancellationToken);
        SelectAfterLoad(result.SelectOnArrival);
    }

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
        Items.Apply(settings);
    }
}
