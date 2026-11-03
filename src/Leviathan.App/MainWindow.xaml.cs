using System.Collections.Specialized;
using System.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Sidebar;
using Leviathan.ViewModels.StatusBar;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// One Leviathan window (spec §4): tab strip in the title bar, navigation row, sidebar, file view, preview pane and status bar.
/// It only shows what the view models hold and forwards every action to the window's command registry, so the buttons, the
/// shortcuts and the action trail all go through the same commands. Slices D2 to D11 replace the placeholder controls here with
/// the real tab strip, breadcrumb bar, column grid and sidebar tree.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is cancelled and released when the window closes, which a Window cannot express as IDisposable.")]
public sealed partial class MainWindow : Window, IShellActions
{
    private static DiagnosticsWindow? s_diagnostics;

    private readonly WindowViewModel _viewModel;
    private readonly CommandRegistry _commands;
    private readonly CancellationTokenSource _closing = new();
    private TabViewModel? _bound;
    private readonly SidebarTree _sidebar;
    private bool _syncingTabs;
    private bool _closed;

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    internal MainWindow(WindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        using (UiTrace.Begin("MainWindow.InitializeComponent"))
        {
            InitializeComponent();
        }

        Title = "Leviathan";

        // The tab strip sits in the title bar; the empty footer of the strip is the part you can drag the window by.
        using (UiTrace.Begin("title bar setup"))
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(DragRegion);
        }

        var host = App.Host;
        _commands = WindowCommands.Create(App.Actions, viewModel, host.Windows, host.Resolver, host.Launcher, this, () => host.Home, host.Favorites, host.NewItems, host.ViewOptions, host.Renamer, host.Transfers, host.DriveCards, host.Transfers, host.Undo, host.FileClipboard, host.Profiles);
        host.DriveCards.Changed += (_, _) => OnUi(UpdateDriveCard);
        host.Undo.Changed += OnUndoChanged;
        host.FileClipboard.Changed += OnClipboardChanged;
        host.Transfers.Finished += OnTransferFinished;

        viewModel.Tabs.Tabs.CollectionChanged += OnTabsChanged;
        viewModel.Tabs.PropertyChanged += OnStripPropertyChanged;
        viewModel.Tabs.Groups.CollectionChanged += OnGroupsCollectionChanged;
        App.StallsChanged += OnStallsChanged;
        Closed += OnClosed;
        Address.Attach(host.AddressBar);
        BindChromeIcons();
        InitHub();
        InitSettingsSheet();
        FileList.ItemInvoked += OnFileInvoked;
        FileList.SortRequested += OnSortRequested;
        FileList.MiddleClicked += OnFileMiddleClicked;
        FileList.KeyDown += OnFileKeyDown;
        FileList.RenameFinished += OnRenameFinished;
        FlowList.RenameFinished += OnRenameFinished;
        FileList.DropRequested += OnItemsDropped;
        FileList.RowContextRequested += OnRowContextRequested;
        FileList.ColumnChooserRequested += async (_, _) => await ShowColumnChooserAsync();
        FlowList.RowContextRequested += OnRowContextRequested;
        FlowList.DropRequested += OnItemsDropped;
        _sidebar = new SidebarTree(host.SidebarSource);
        Sidebar.ItemsSource = _sidebar.Rows;
        _sidebar.SelectionChanged += OnSidebarSelectionChanged;
        host.Favorites.Changed += OnFavoritesChanged;
        Address.FavoriteToggleRequested += (_, _) => Run(CommandIds.FavoriteToggle);
        Address.FavoriteMenuRequested += OnAddressFavoriteMenu;
        FlowList.ItemInvoked += OnFileInvoked;
        FlowList.MiddleClicked += OnFileMiddleClicked;
        FlowList.KeyDown += OnFileKeyDown;
        FlowList.ZoomRequested += (_, notches) => Run(CommandIds.ViewZoom, parameter: notches);
        Address.Submitted += OnAddressSubmitted;
        Address.NavigationRequested += OnAddressNavigation;
        Address.DropRequested += OnItemsDropped;
        Address.EditEnded += OnAddressEditEnded;
        host.CloudFoldersChanged += OnCloudFoldersChanged;
        TabsView.Loaded += (_, _) => UpdateTitleBarRegions();
        TabsView.LayoutUpdated += OnTabsLayoutUpdated;
        AppWindow.Closing += (_, _) => App.NotifyWindowClosing(_viewModel);
        AppWindow.Changed += OnAppWindowChanged;
        Activated += (_, _) => CaptureBounds(AppWindow);

        using (UiTrace.Begin("MainWindow ctor: SyncTabs"))
        {
            SyncTabs();
        }

        ShowStalls();
        InitializeCommandBar();
        InitializeSidebarSplitter();
        _ = LoadSidebarAsync();
    }

    /// <inheritdoc />
    public void FocusAddressBar() => OnUi(() => Address.BeginEdit());

    /// <inheritdoc />
    public void OpenDiagnostics() => OnUi(() =>
    {
        if (s_diagnostics is null)
        {
            s_diagnostics = new DiagnosticsWindow();
            s_diagnostics.Closed += (_, _) => s_diagnostics = null;
        }

        s_diagnostics.Activate();
    });

    /// <inheritdoc />
    public void ShowMessage(string message) => OnUi(() => MessageText.Text = message);

    private void OnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    // Commands ---------------------------------------------------------------------------------------------------------

    /// <summary>Runs a command. Cancellation and expected I/O failures end quietly in the status bar; anything else is a bug and reaches the crash handler.</summary>
    private async void Run(CommandId id, string? target = null, object? parameter = null)
    {
        try
        {
            MessageText.Text = string.Empty;
            await _commands.ExecuteAsync(id, new CommandContext(target, parameter), _closing.Token);
        }
        catch (OperationCanceledException)
        {
            // The window closed or the operation was replaced; nothing to report.
        }
        catch (LocationException ex)
        {
            MessageText.Text = ex.Message;
        }
        finally
        {
            RefreshNavigationButtons();
        }
    }

    private void RefreshNavigationButtons()
    {
        BackButton.IsEnabled = _commands.CanExecute(CommandIds.NavBack);
        ForwardButton.IsEnabled = _commands.CanExecute(CommandIds.NavForward);
        UpButton.IsEnabled = _commands.CanExecute(CommandIds.NavUp);
        ReloadButton.IsEnabled = _commands.CanExecute(CommandIds.NavReload);
        RefreshCommandBar();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavBack);

    private void OnForwardClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavForward);

    private void OnUpClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavUp);

    private void OnReloadClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavReload);

    private void OnNewWindowClick(object sender, RoutedEventArgs e) => Run(CommandIds.WindowNew);

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e) => Run(CommandIds.AppDiagnostics);

    private void OnAddTabClick(TabView sender, object args) => Run(CommandIds.TabNew);

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        UiTrace.Mark($"tab close requested: {(args.Tab.Tag as TabViewModel)?.Title}");
        if (args.Tab.Tag is TabViewModel tab)
        {
            App.Actions.Record("tab.close", tab.History.Current.Value);
            _viewModel.Tabs.Close(tab, force: true);
        }
    }

    // Keyboard ---------------------------------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Esc closes the settings sheet (spec 17.2) wherever the focus is, even in its search box.
        if (e.Key == VirtualKey.Escape && HandleSettingsEscape())
        {
            e.Handled = true;
            return;
        }

        // Esc closes an unpinned status hub (spec 16.1), unless the key belongs to a text box.
        if (e.Key == VirtualKey.Escape && HandleHubEscape())
        {
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+digit is a layout shortcut (spec §8.1), plain Ctrl+digit selects a tab.
        if (KeyTranslator.IsDown(VirtualKey.Control) && !KeyTranslator.IsDown(VirtualKey.Menu) && !KeyTranslator.IsDown(VirtualKey.Shift)
            && KeyTranslator.DigitOf(e.Key) is > 0 and var digit)
        {
            e.Handled = true;
            Run(CommandIds.TabSelect, parameter: digit);
            return;
        }

        if (KeyTranslator.ToGesture(e.Key) is { } gesture && App.Host.Keys.Resolve(gesture, _viewModel.Tabs.Selected?.IsSplit == true) is { } command)
        {
            // In a text box these keys edit the text: Ctrl+A selects it, Del deletes a character, Ctrl+Z undoes typing. They must
            // never reach the files behind it.
            if (IsEditingText() && (command == CommandIds.SelectAll || command == CommandIds.EditRename || command == CommandIds.EditDelete
                || command == CommandIds.EditDeletePermanent || command == CommandIds.EditUndo || command == CommandIds.EditRedo
                || command == CommandIds.EditCut || command == CommandIds.EditCopy || command == CommandIds.EditPaste))
            {
                return;
            }

            if (_commands.CanExecute(command))
            {
                e.Handled = true;
                Run(command);
            }
        }
    }

    // Address bar ------------------------------------------------------------------------------------------------------

    private void OnAddressSubmitted(object? sender, string text) => Run(CommandIds.NavGo, text);

    private void OnAddressNavigation(object? sender, AddressNavigation request)
    {
        if (request.NewTab)
        {
            Run(CommandIds.TabOpenInNewTab, request.Location.Value, false);
        }
        else
        {
            Run(CommandIds.NavGo, request.Location.Value);
        }
    }

    private void OnAddressEditEnded(object? sender, EventArgs e) => FocusFiles();

    /// <summary>Focuses whichever file view the tab's layout shows.</summary>
    private void FocusFiles()
    {
        if (FlowList.Visibility == Visibility.Visible)
        {
            FlowList.FocusGrid();
        }
        else
        {
            FileList.FocusGrid();
        }
    }

    /// <summary>Shows the Details grid or the List / Thumbnails view for the tab's layout, keeping the keyboard focus in the file view.</summary>
    private void ApplyLayout()
    {
        var flow = _bound is { } tab && ItemFlow.IsFlow(tab.Layout);
        if ((FlowList.Visibility == Visibility.Visible) == flow)
        {
            return;
        }

        var hadFocus = FileList.HasKeyboardFocus || FlowList.HasKeyboardFocus;
        FlowList.Visibility = flow ? Visibility.Visible : Visibility.Collapsed;
        FileList.Visibility = flow ? Visibility.Collapsed : Visibility.Visible;
        if (flow)
        {
            // The view skipped scrolling to the selection while it was collapsed; do it now that elements can be created.
            DispatcherQueue.TryEnqueue(FlowList.EnsureVisible);
        }

        if (hadFocus)
        {
            FocusFiles();
        }
    }

    private void OnTabViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Layout))
        {
            OnUi(() =>
            {
                ApplyLayout();
                UpdateLayoutSwitch();
            });
        }
        else if (e.PropertyName == nameof(TabViewModel.ThumbnailSize))
        {
            OnUi(UpdateLayoutSwitch);
        }
        else if (e.PropertyName == nameof(TabViewModel.FilterText))
        {
            OnUi(SyncFilterBox);
        }
    }

    private void OnCloudFoldersChanged(object? sender, EventArgs e) => OnUi(Address.RefreshStatus);

    // Tabs -------------------------------------------------------------------------------------------------------------

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnUi(SyncTabs);

    private void OnStripPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabStripViewModel.Selected))
        {
            OnUi(SyncSelectedTab);
        }
    }

    /// <summary>
    /// Makes the tab strip show exactly the view model's tabs, in order, changing as little as possible: a tab being dragged
    /// is not removed and re-added underneath its own drag (that crashed WinUI with a stowed E_NOINTERFACE).
    /// </summary>
    private void SyncTabs()
    {
        _syncingTabs = true;
        try
        {
            var tabs = _viewModel.Tabs.Tabs.ToList();
            foreach (var removed in _tabUi.Keys.Except(tabs).ToList())
            {
                removed.PropertyChanged -= OnTabPropertyChanged;
                TabsView.TabItems.Remove(_tabUi[removed].Item);
                _tabUi.Remove(removed);
            }

            RemoveGoneChips();

            // What the strip should hold: each tab in order, with its group's chip in front of the group's first tab.
            var wanted = new List<TabViewItem>(tabs.Count + _viewModel.Tabs.Groups.Count);
            var chipped = new HashSet<TabGroup>();
            foreach (var tab in tabs)
            {
                if (!_tabUi.TryGetValue(tab, out var ui))
                {
                    ui = new TabItemUi(tab);
                    ui.Item.ContextFlyout = BuildTabMenu(tab);
                    MakeTabAFileDropTarget(ui.Item);
                    tab.PropertyChanged += OnTabPropertyChanged;
                    _tabUi[tab] = ui;
                }

                var group = _viewModel.Tabs.GroupOf(tab);
                ui.Refresh(group);
                if (group is not null && chipped.Add(group))
                {
                    var chip = EnsureChip(group);
                    chip.Refresh(_viewModel.Tabs.TabsOf(group).Count);
                    wanted.Add(chip.Item);
                }

                wanted.Add(ui.Item);
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                if (i >= TabsView.TabItems.Count || !ReferenceEquals(TabsView.TabItems[i], wanted[i]))
                {
                    var existing = TabsView.TabItems.IndexOf(wanted[i]);
                    if (existing >= 0)
                    {
                        TabsView.TabItems.RemoveAt(existing);
                    }

                    TabsView.TabItems.Insert(Math.Min(i, TabsView.TabItems.Count), wanted[i]);
                }
            }
        }
        finally
        {
            _syncingTabs = false;
        }

        SyncSelectedTab();
    }

    private void SyncSelectedTab()
    {
        var selected = _viewModel.Tabs.Selected;
        _syncingTabs = true;
        try
        {
            TabsView.SelectedItem = selected is not null && _tabUi.TryGetValue(selected, out var ui) ? ui.Item : null;
        }
        finally
        {
            _syncingTabs = false;
        }

        BindTab(selected);
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabs)
        {
            return;
        }

        if (TabsView.SelectedItem is TabViewItem { Tag: TabViewModel tab })
        {
            _viewModel.Tabs.Select(tab);
        }
        else if (TabsView.SelectedItem is TabViewItem { Tag: TabGroup group })
        {
            // A chip is not a place to be: the click folds or unfolds the group and the selection stays on the real tab.
            HandleChipSelected(group);
        }
    }

    /// <summary>Shows <paramref name="tab"/> in the body: its rows, address, state and status. Lists the folder if the tab was restored without one.</summary>
    private void BindTab(TabViewModel? tab)
    {
        if (ReferenceEquals(_bound, tab))
        {
            return;
        }

        if (_bound is not null)
        {
            _bound.Folder.PropertyChanged -= OnFolderPropertyChanged;
            _bound.Items.PropertyChanged -= OnItemsPropertyChanged;
            _bound.Selection.Changed -= OnSelectionChanged;
            _bound.Address.PropertyChanged -= OnAddressLocationChanged;
            _bound.PropertyChanged -= OnTabViewChanged;
        }

        _bound = tab;
        if (tab is null)
        {
            FileList.Bind(null);
            FlowList.Bind(null);
            Address.Bind(null);
            SyncFilterBox();
            RefreshCommandBar();
            return;
        }

        using var bind = UiTrace.Begin("BindTab");
        FileList.Bind(tab);
        FlowList.Bind(tab);
        tab.PropertyChanged += OnTabViewChanged;
        ApplyLayout();
        tab.Selection.Changed += OnSelectionChanged;
        tab.Folder.PropertyChanged += OnFolderPropertyChanged;
        tab.Items.PropertyChanged += OnItemsPropertyChanged;
        Address.Bind(tab);
        tab.Address.PropertyChanged += OnAddressLocationChanged;
        UpdateState();
        UpdateStatus();
        SyncFilterBox();
        RefreshNavigationButtons();
        SyncSidebar();
        UpdateFavoriteStar();
        if (tab.Folder.State == FolderLoadState.Idle)
        {
            Run(CommandIds.NavReload);
        }
    }

    private void OnFolderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnUi(() =>
        {
            using var scope = UiTrace.Begin($"folder changed: {e.PropertyName}");
            if (_bound?.Folder.State is not (FolderLoadState.Idle or FolderLoadState.Loading))
            {
                App.ReportReady(ReadyPart.FirstListing);
            }

            UpdateState();
            UpdateStatus();
            RefreshNavigationButtons();
        });
    }

    private void OnAddressLocationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.Navigation.AddressBarViewModel.Location))
        {
            OnUi(() =>
            {
                RefreshNavigationButtons();
                SyncSidebar();
                UpdateFavoriteStar();
            });
        }
    }

    private void OnItemsPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnUi(() =>
    {
        UpdateStatus();
        UpdateState();
    });

    // File view --------------------------------------------------------------------------------------------------------

    private void OnFileInvoked(object? sender, FileEntry entry) => Run(CommandIds.ItemOpen, entry.Location.Value, entry);

    private void OnSortRequested(object? sender, DetailsGrid.SortRequest request)
    {
        if (_bound is { } tab)
        {
            App.Actions.Record("view.sort", request.ColumnId);
            tab.SortBy(request.ColumnId, request.Additive);
        }
    }

    private void OnFileKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Back && !KeyTranslator.IsDown(VirtualKey.Control))
        {
            e.Handled = true;
            Run(CommandIds.NavBack);
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        UpdateStatus();
        RefreshCommandBar();
    });

    /// <summary>The rename box closed: rename (Enter, Tab, click away) or leave the name alone (Esc).</summary>
    private void OnRenameFinished(object? sender, RenameResult result)
    {
        if (_bound is null)
        {
            return;
        }

        if (result.Commit)
        {
            Run(CommandIds.RenameCommit, result.Text, result.Next);
        }
        else
        {
            _bound.CancelRename();
            FocusFiles();
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavReload);

    private void UpdateState()
    {
        var tab = _bound;
        var state = tab?.Folder.State ?? FolderLoadState.Idle;
        var hasRows = tab?.Items.ShownCount > 0;
        var (title, detail, busy, glyph, retry) = state switch
        {
            FolderLoadState.Loading when !hasRows => (string.Empty, string.Empty, false, string.Empty, false),
            FolderLoadState.Empty => ("This folder is empty", string.Empty, false, "", false),
            FolderLoadState.NotFound => ("Folder not found", tab?.Folder.Message ?? string.Empty, false, "", false),
            FolderLoadState.AccessDenied => ("Access denied", tab?.Folder.Message ?? string.Empty, false, "", false),
            FolderLoadState.Offline => ("Not responding", tab?.Folder.Message ?? string.Empty, false, "", true),
            FolderLoadState.Error => ("This folder could not be opened", tab?.Folder.Message ?? string.Empty, false, "", tab?.Folder.IsRetryable == true),
            FolderLoadState.Cancelled when !hasRows => ("Listing cancelled", "Press F5 to list this folder again.", false, "", true),
            _ => (string.Empty, string.Empty, false, string.Empty, false),
        };

        StatePanel.Visibility = title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StateTitle.Text = title;
        StateDetail.Text = detail;
        StateDetail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StateRing.IsActive = busy;
        StateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StateIcon.Content = glyph.Length == 0 ? null : Icons.OfGlyph(glyph, 32);
        StateIcon.Visibility = glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus()
    {
        if (_bound is null)
        {
            StatusText.Text = string.Empty;
            SelectionPart.Visibility = FilterPart.Visibility = HealthText.Visibility = FreePart.Visibility = DriveCard.Visibility = NoticePart.Visibility = Visibility.Collapsed;
            return;
        }

        var items = _bound.Items;
        StatusText.Text = StatusBarText.Items(items.TotalCount, items.FolderCount, items.HiddenCount);

        var selection = StatusBarText.Selection(_bound.Selection.Count, _bound.Selection.SelectedBytes);
        SelectionText.Text = selection;
        SelectionPart.Visibility = selection.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var filter = StatusBarText.Filter(_bound.FilterText, items.ShownCount, items.TotalCount);
        FilterText.Text = filter;
        FilterPart.Visibility = filter.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowListingNotice(_bound.Folder.Notice);

        var health = StatusBarText.Health(_bound.History.Current, _bound.IsOffline);
        HealthText.Text = health;
        HealthText.Visibility = health.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var drive = StatusBarText.DriveOf(_bound.History.Current, _drives);
        var free = StatusBarText.FreeSpace(drive);
        FreeText.Text = free;
        FreeBar.Value = (drive?.UsedFraction ?? 0) * 100;
        FreeBar.Foreground = SidebarFormat.BarBrush(drive?.IsLowOnSpace == true);
        FreePart.Visibility = free.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayoutSwitch();
        UpdateDriveCard();
    }

    private bool _syncingSlider;

    /// <summary>Highlights the active layout and shows the size slider in Thumbnails.</summary>
    private void UpdateLayoutSwitch()
    {
        var layout = _bound?.Layout;
        void Mark(Button button, bool on) => button.Background = on
            ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Mark(DetailsSwitch, layout == ViewModels.Views.ViewLayout.Details);
        Mark(ListSwitch, layout == ViewModels.Views.ViewLayout.List);
        Mark(ThumbsSwitch, layout == ViewModels.Views.ViewLayout.Thumbnails);
        SizeSlider.Visibility = layout == ViewModels.Views.ViewLayout.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
        if (_bound is { } tab && Math.Abs(SizeSlider.Value - tab.ThumbnailSize) > 0.5)
        {
            _syncingSlider = true;
            SizeSlider.Value = tab.ThumbnailSize;
            _syncingSlider = false;
        }
    }

    private void OnStatusItemsClick(object sender, RoutedEventArgs e) => Run(CommandIds.ViewHidden);

    private void OnStatusSelectionClick(object sender, RoutedEventArgs e) => Run(CommandIds.SelectNone);

    private void OnStatusFilterClick(object sender, RoutedEventArgs e) => Run(CommandIds.FilterSet, string.Empty);

    private void OnLayoutSwitchClick(object sender, RoutedEventArgs e) => Run(((string)((FrameworkElement)sender).Tag) switch
    {
        "list" => CommandIds.ViewList,
        "thumbnails" => CommandIds.ViewThumbnails,
        _ => CommandIds.ViewDetails,
    });

    private void OnSizeSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingSlider && _bound is { Layout: ViewModels.Views.ViewLayout.Thumbnails })
        {
            Run(CommandIds.ViewThumbnailSize, parameter: (int)e.NewValue);
        }
    }

    // Sidebar ----------------------------------------------------------------------------------------------------------

    private IReadOnlyList<SidebarPlace> _commonPlaces = [];
    private IReadOnlyList<DriveDetails> _drives = [];

    private async Task LoadSidebarAsync()
    {
        var host = App.Host;
        try
        {
            // Known folders and drive details read the registry and the disks: never on the UI thread.
            _commonPlaces = await Task.Run(
                () => new[]
                {
                    (KnownFolder.Home, "Home", SidebarIcon.Home),
                    (KnownFolder.Desktop, "Desktop", SidebarIcon.Desktop),
                    (KnownFolder.Documents, "Documents", SidebarIcon.Documents),
                    (KnownFolder.Downloads, "Downloads", SidebarIcon.Downloads),
                    (KnownFolder.Pictures, "Pictures", SidebarIcon.Pictures),
                    (KnownFolder.Videos, "Videos", SidebarIcon.Videos),
                    (KnownFolder.Music, "Music", SidebarIcon.Music),
                }
                .Select(k => (Location: host.KnownFolders.Get(k.Item1), k.Item2, k.Item3))
                .Where(k => k.Location is not null)
                .Select(k => new SidebarPlace(k.Item2, k.Location!, k.Item3))
                .ToList(),
                _closing.Token);
            _drives = await host.Volumes.GetDrivesAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using (UiTrace.Begin("Sidebar.Build"))
        {
            RebuildSidebar();
        }

        App.ReportReady(ReadyPart.Sidebar);
    }

    /// <summary>Builds the tree from the data read at startup and the current favorites; expanded folders are not kept, the tree follows the current folder again.</summary>
    private void RebuildSidebar()
    {
        var host = App.Host;
        _sidebar.SetSections(SidebarSections.Build(host.Favorites.Items, _commonPlaces, _drives, host.CloudProviders));
        SyncSidebar();
        UpdateStatus();
    }

    private void OnFavoritesChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        RebuildSidebar();
        UpdateFavoriteStar();
    });

    private void SyncSidebar()
    {
        if (_bound is { } tab && _sidebar.Rows.Count > 0)
        {
            _ = SyncSidebarAsync(tab.History.Current);
        }
    }

    private async Task SyncSidebarAsync(Core.Paths.LocationPath location)
    {
        try
        {
            using var scope = UiTrace.Begin("Sidebar.Sync");
            await _sidebar.SyncAsync(location, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
    }

    private void OnSidebarSelectionChanged(object? sender, EventArgs e)
    {
        if (_sidebar.Selected is { } node && _sidebar.Rows.Contains(node))
        {
            using var scope = UiTrace.Begin("Sidebar.ScrollIntoView");
            Sidebar.ScrollIntoView(node);
        }
    }

    private async void OnSidebarItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SidebarNode node)
        {
            return;
        }

        switch (node.Kind)
        {
            case SidebarNodeKind.Section:
                await _sidebar.ToggleAsync(node, _closing.Token);
                break;
            case SidebarNodeKind.Message when node.IsRetry:
                await _sidebar.RetryAsync(node, _closing.Token);
                break;
            case SidebarNodeKind.Place or SidebarNodeKind.Folder when node.Location is { } where:
                App.Actions.Record("sidebar.open", where.Value);
                Run(CommandIds.NavGo, where.Value);
                break;
        }
    }

    private async void OnSidebarChevronClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SidebarNode node)
        {
            await _sidebar.ToggleAsync(node, _closing.Token);
        }
    }

    private async void OnSidebarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((e.OriginalSource as ListViewItem)?.Content is not SidebarNode node)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Right when node.CanExpand && !node.IsExpanded:
                e.Handled = true;
                await _sidebar.ExpandAsync(node, _closing.Token);
                break;
            case VirtualKey.Left when node.IsExpanded:
                e.Handled = true;
                _sidebar.Collapse(node);
                break;
            case VirtualKey.Left when node.Parent is { } parent && Sidebar.ContainerFromItem(parent) is ListViewItem container:
                e.Handled = true;
                container.Focus(FocusState.Keyboard);
                break;
        }
    }

    private void OnSidebarRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SidebarNode { Location: { } where } node)
        {
            return;
        }

        e.Handled = true;
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open in new tab" };
        open.Click += (_, _) => Run(CommandIds.TabOpenInNewTab, where.Value, true);
        menu.Items.Add(open);
        var pinned = App.Host.Favorites.Contains(where);
        var pin = new MenuFlyoutItem { Text = pinned ? "Remove from Favorites" : "Add to Favorites" };
        pin.Click += (_, _) => App.Host.Favorites.Toggle(where);
        menu.Items.Add(pin);
        if (node.Parent?.Title == "Favorites")
        {
            var rename = new MenuFlyoutItem { Text = "Rename…" };
            rename.Click += async (_, _) => await RenameFavoriteAsync(where, node.Title);
            menu.Items.Add(rename);
        }

        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
    }

    private async Task RenameFavoriteAsync(Core.Paths.LocationPath where, string current)
    {
        var box = new TextBox { Text = current, SelectionStart = 0, SelectionLength = current.Length };
        var dialog = new ContentDialog
        {
            Title = "Rename favorite",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Run(CommandIds.FavoriteRename, where.Value, box.Text);
        }
    }

    // Status -----------------------------------------------------------------------------------------------------------

    private void OnStallsChanged(object? sender, EventArgs e) => OnUi(ShowStalls);

    private void ShowStalls() => StallText.Text = App.StallCount > 0 ? $"UI stalls: {App.StallCount}" : string.Empty;

    /// <summary>Keeps the view model's bounds equal to the real window, so a tab torn out of this window opens at its size (and a saved session can restore it).</summary>
    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange)
        {
            CaptureBounds(sender);
        }
    }

    private bool _maximized;

    private void CaptureBounds(Microsoft.UI.Windowing.AppWindow window)
    {
        if (_closed)
        {
            return;
        }

        if (window.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized })
        {
            // Keep the restored rectangle and remember the window was maximized (spec §17.1).
            if (!_maximized)
            {
                _maximized = true;
                _viewModel.Bounds = _viewModel.Bounds with { IsMaximized = true };
            }

            return;
        }

        if (window.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized })
        {
            return;
        }

        _viewModel.Bounds = new ViewModels.Windows.WindowBounds(new ViewModels.Windows.ScreenRect(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height));
        _maximized = false;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        UiTrace.Mark($"window closed (tabs: {_viewModel.Tabs.Tabs.Count})");
        // A closed window throws on Content and AppWindow; layout events can still arrive during teardown.
        _closed = true;
        TabsView.LayoutUpdated -= OnTabsLayoutUpdated;
        App.StallsChanged -= OnStallsChanged;
        App.Host.CloudFoldersChanged -= OnCloudFoldersChanged;
        App.Host.Transfers.Finished -= OnTransferFinished;
        App.Host.Icons.Changed -= OnIconsChanged;
        App.Host.Operations.Changed -= OnOperationsChanged;
        App.Host.Activity.Changed -= OnActivityChanged;
        App.Host.Background.Changed -= OnBackgroundChanged;
        _settingsSheet?.Detach();
        _hubPanel?.Detach();
        _hubWindow?.Close();
        App.Host.Undo.Changed -= OnUndoChanged;
        App.Host.FileClipboard.Changed -= OnClipboardChanged;
        _viewModel.Tabs.Tabs.CollectionChanged -= OnTabsChanged;
        _viewModel.Tabs.PropertyChanged -= OnStripPropertyChanged;
        _viewModel.Tabs.Groups.CollectionChanged -= OnGroupsCollectionChanged;
        _closing.Cancel();
        _closing.Dispose();
    }
}
