using System.ComponentModel;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>
/// The split view of a tab (spec §15.1): one pane, or two next to each other or above each other. Each pane has its own path, history,
/// layout and selection (it is a <see cref="TabViewModel"/> of its own); the address bar, command bar, status bar and preview follow
/// the pane that has the focus, so everything that already works on "the current tab" works on the focused pane without knowing.
/// </summary>
public sealed partial class MainWindow
{
    private const double PaneGapSize = 6;

    private TabViewModel? _paneStrip;
    private TabViewModel? _boundSecond;
    private readonly List<TabViewModel> _stateHooked = [];

    /// <summary>Wires the second pane's views like the first's, and makes touching a pane give it the focus.</summary>
    private void InitPanes()
    {
        // The focus changes first, so a handler that acts on "the current tab" acts on the pane that raised the event.
        FileList2.DropRequested += (_, _) => OnPaneFocused(1);
        FlowList2.DropRequested += (_, _) => OnPaneFocused(1);
        FileList.DropRequested += (_, _) => OnPaneFocused(0);
        FlowList.DropRequested += (_, _) => OnPaneFocused(0);

        FileList2.ItemInvoked += OnFileInvoked;
        FileList2.SortRequested += OnSortRequested;
        FileList2.MiddleClicked += OnFileMiddleClicked;
        FileList2.KeyDown += OnFileKeyDown;
        FileList2.RenameFinished += OnRenameFinished;
        FileList2.DropRequested += OnItemsDropped;
        FileList2.RowContextRequested += OnRowContextRequested;
        FileList2.ColumnChooserRequested += async (_, _) => await ShowColumnChooserAsync();
        FlowList2.RenameFinished += OnRenameFinished;
        FlowList2.RowContextRequested += OnRowContextRequested;
        FlowList2.DropRequested += OnItemsDropped;
        FlowList2.ItemInvoked += OnFileInvoked;
        FlowList2.MiddleClicked += OnFileMiddleClicked;
        FlowList2.KeyDown += OnFileKeyDown;
        FlowList2.ZoomRequested += (_, notches) => Run(CommandIds.ViewZoom, parameter: notches);

        Pane1.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => OnPaneFocused(0)), handledEventsToo: true);
        Pane2.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => OnPaneFocused(1)), handledEventsToo: true);
        Pane1.GotFocus += (_, _) => OnPaneFocused(0);
        Pane2.GotFocus += (_, _) => OnPaneFocused(1);
    }

    /// <summary>A pane was touched or took the keyboard focus: the bars follow it.</summary>
    private void OnPaneFocused(int pane)
    {
        if (_viewModel.Tabs.Selected is { IsSplit: true } tab && tab.FocusedPane != pane)
        {
            App.Actions.Record("pane.focus", pane.ToString(System.Globalization.CultureInfo.InvariantCulture));
            tab.FocusPane(pane);
        }
    }

    private void RunPaneCommand(CommandId command)
    {
        Run(command);

        // A toggle button flips itself on click; the state of the tab is what it shows.
        DispatcherQueue.TryEnqueue(UpdatePaneButtons);
    }

    /// <summary>The tab or its panes changed: show the panes, and let the bars follow the focused one.</summary>
    private void OnStripTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Panes) or nameof(TabViewModel.Secondary) or nameof(TabViewModel.FocusedPane))
        {
            OnUi(RefreshPanes);
        }
    }

    // A pane's listing state (loading, empty, error) is drawn over that pane, whichever pane has the focus.
    private void OnPaneStateChanged(object? sender, PropertyChangedEventArgs e) => OnUi(UpdateState);

    /// <summary>
    /// Shows the selected tab's panes and binds the address bar, status bar and the rest to the focused one. Called when the selected
    /// tab changes, when a split opens or closes, and when the focus moves from one pane to the other.
    /// </summary>
    private void RefreshPanes()
    {
        var strip = _viewModel.Tabs.Selected;
        if (!ReferenceEquals(_paneStrip, strip))
        {
            if (_paneStrip is not null)
            {
                _paneStrip.PropertyChanged -= OnStripTabChanged;
            }

            _paneStrip = strip;
            if (strip is not null)
            {
                strip.PropertyChanged += OnStripTabChanged;
            }
        }

        BindPaneViews(strip);
        LayoutPanes(strip);
        BindTab(strip?.Focused);
        ApplyLayout();
        UpdatePaneButtons();
        UpdateFrames();
        UpdateState();
    }

    private void BindPaneViews(TabViewModel? strip)
    {
        FileList.Bind(strip);
        FlowList.Bind(strip);
        var second = strip?.Secondary;
        if (!ReferenceEquals(_boundSecond, second))
        {
            _boundSecond = second;
            FileList2.Bind(second);
            FlowList2.Bind(second);
        }

        foreach (var hooked in _stateHooked)
        {
            hooked.Folder.PropertyChanged -= OnPaneStateChanged;
            hooked.Items.PropertyChanged -= OnPaneStateChanged;
        }

        _stateHooked.Clear();
        foreach (var pane in new[] { strip, second }.OfType<TabViewModel>())
        {
            pane.Folder.PropertyChanged += OnPaneStateChanged;
            pane.Items.PropertyChanged += OnPaneStateChanged;
            _stateHooked.Add(pane);

            // A tab restored from a session is listed when it is first shown, never before (spec §17.1).
            if (pane.Folder.State == FolderLoadState.Idle)
            {
                _ = ListPaneAsync(pane);
            }
        }
    }

    private async Task ListPaneAsync(TabViewModel pane)
    {
        try
        {
            await pane.ReloadAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
    }

    // Places the first pane alone, or the two with a gap between them, side by side or one above the other.
    private void LayoutPanes(TabViewModel? strip)
    {
        var layout = strip?.IsSplit == true ? strip.Panes : PaneLayout.Single;
        PaneHost.ColumnDefinitions.Clear();
        PaneHost.RowDefinitions.Clear();
        var split = layout != PaneLayout.Single;
        Pane2.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        PaneGap.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(Pane1, 0);
        Grid.SetColumn(Pane1, 0);
        if (!split)
        {
            return;
        }

        var star = new GridLength(1, GridUnitType.Star);
        var gap = new GridLength(PaneGapSize);
        if (layout == PaneLayout.SideBySide)
        {
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = gap });
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            Grid.SetColumn(PaneGap, 1);
            Grid.SetColumn(Pane2, 2);
            Grid.SetRow(PaneGap, 0);
            Grid.SetRow(Pane2, 0);
        }
        else
        {
            PaneHost.RowDefinitions.Add(new RowDefinition { Height = star });
            PaneHost.RowDefinitions.Add(new RowDefinition { Height = gap });
            PaneHost.RowDefinitions.Add(new RowDefinition { Height = star });
            Grid.SetRow(PaneGap, 1);
            Grid.SetRow(Pane2, 2);
            Grid.SetColumn(PaneGap, 0);
            Grid.SetColumn(Pane2, 0);
        }
    }

    // Which of the three buttons shows the tab's layout.
    private void UpdatePaneButtons()
    {
        var layout = _viewModel.Tabs.Selected is { IsSplit: true } tab ? tab.Panes : PaneLayout.Single;
        PaneSingleButton.IsChecked = layout == PaneLayout.Single;
        PaneSideButton.IsChecked = layout == PaneLayout.SideBySide;
        PaneStackedButton.IsChecked = layout == PaneLayout.TopBottom;
    }

    // A thin accent frame on the pane that has the focus, only while there are two panes.
    private void UpdateFrames()
    {
        var split = _viewModel.Tabs.Selected is { IsSplit: true };
        var focused = _viewModel.Tabs.Selected?.FocusedPane ?? 0;
        var accent = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var brush) && brush is Brush found
            ? found
            : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Pane1Frame.BorderBrush = split && focused == 0 ? accent : none;
        Pane2Frame.BorderBrush = split && focused == 1 ? accent : none;
    }

    // The second pane's listing state: the title and detail over its rows, and a way to try again.
    private void UpdateState2()
    {
        var tab = _viewModel.Tabs.Selected?.Secondary;
        var state = tab?.Folder.State ?? FolderLoadState.Idle;
        var hasRows = tab?.Items.ShownCount > 0;
        var (title, detail, retry) = state switch
        {
            FolderLoadState.Empty => ("This folder is empty", string.Empty, false),
            FolderLoadState.NotFound => ("Folder not found", tab?.Folder.Message ?? string.Empty, false),
            FolderLoadState.AccessDenied => ("Access denied", tab?.Folder.Message ?? string.Empty, false),
            FolderLoadState.Offline => (tab?.Folder.NeedsSignIn == true ? "Sign-in required" : "Not responding", tab?.Folder.Message ?? string.Empty, true),
            FolderLoadState.Error => ("This folder could not be opened", tab?.Folder.Message ?? string.Empty, tab?.Folder.IsRetryable == true),
            FolderLoadState.Cancelled when !hasRows => ("Listing cancelled", "Press F5 to list this folder again.", true),
            _ => (string.Empty, string.Empty, false),
        };

        StatePanel2.Visibility = title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        State2Title.Text = title;
        State2Detail.Text = detail;
        State2Detail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Retry2Button.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRetry2Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Tabs.Selected?.FocusPane(1);
        Run(CommandIds.NavReload);
    }
}
