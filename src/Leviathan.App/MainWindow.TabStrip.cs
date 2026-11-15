using System.ComponentModel;
using System.Runtime.InteropServices;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Tabs;
using Microsoft.UI;
using Microsoft.UI.Input;using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace Leviathan.App;

/// <summary>What one tab looks like in the strip: folder icon, label (hidden when pinned), lock, busy and offline marks, and the colour bar (spec §4.1).</summary>
internal sealed class TabItemUi
{
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _pin = new() { Content = Icons.Of("pin", 12), IsTabStop = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _lock = new() { Content = Icons.Of("lock", 12), IsTabStop = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _busy = new() { Width = 12, Height = 12, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _offline = new() { Content = Icons.Of("warning", 14, OfflineColor), IsTabStop = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _header;
    private string _tip = string.Empty;
    private readonly Border _accent = new() { Height = 2, CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Border _groupBar = new() { Height = 3, CornerRadius = new CornerRadius(1.5), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 1, 0, 0) };

    public TabItemUi(TabViewModel tab)
    {
        Tab = tab;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(_pin);
        content.Children.Add(_lock);
        content.Children.Add(_title);
        content.Children.Add(_busy);
        content.Children.Add(_offline);
        var header = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        _header = header;
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.Children.Add(content);
        Grid.SetRow(_accent, 1);
        header.Children.Add(_accent);
        Grid.SetRow(_groupBar, 2);
        header.Children.Add(_groupBar);
        Item = new TabViewItem { Tag = tab, Header = header, IconSource = Icons.SourceOf("folder") };

        // The TabView puts its own tooltip on a tab when the tab's template is applied, which replaces one set before that. Set it again once
        // the tab is loaded, and keep one on the header as well, which the TabView leaves alone.
        Item.Loaded += (_, _) => ApplyTip();
        Refresh();
    }

    // The warning mark of a tab whose location does not answer: dark orange on both themes.
    private const string OfflineColor = "#D9730D";

    /// <summary>Draws the tab's icons again after the icon pack or the theme changed.</summary>
    public void RefreshIcons()
    {
        _pin.Content = Icons.Of("pin", 12);
        _lock.Content = Icons.Of("lock", 12);
        _offline.Content = Icons.Of("warning", 14, OfflineColor);
        Item.IconSource = Icons.SourceOf("folder");
    }

    /// <summary>Pinned tabs show their names (setting); off, only the icon and the pin show and the name is in the tooltip.</summary>
    public static bool ShowPinnedNames { get; set; }

    public TabViewModel Tab { get; }

    public TabViewItem Item { get; }

    /// <summary>Redraws the tab. <paramref name="group"/> is the tab's group: its colour runs as a bar under the tab (spec §4.1), and a folded group hides its tabs.</summary>
    public void Refresh(TabGroup? group = null)
    {
        var tab = Tab;
        Item.Visibility = group is { IsCollapsed: true } ? Visibility.Collapsed : Visibility.Visible;
        if (group is not null && TryParseColor(group.Color, out var groupColor))
        {
            _groupBar.Background = new SolidColorBrush(groupColor);
            _groupBar.Visibility = Visibility.Visible;
        }
        else
        {
            _groupBar.Visibility = Visibility.Collapsed;
        }

        _title.Text = tab.Title;
        _title.Visibility = tab.IsPinned && !ShowPinnedNames ? Visibility.Collapsed : Visibility.Visible;
        _pin.Visibility = tab.IsPinned ? Visibility.Visible : Visibility.Collapsed;
        _tip = TipOf(tab);
        ApplyTip();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Item, tab.IsPinned ? $"{tab.Title}, pinned" : tab.Title);
        Item.IsClosable = !tab.IsPinned;
        _lock.Visibility = tab.IsLocked ? Visibility.Visible : Visibility.Collapsed;
        _busy.IsActive = tab.IsBusy;
        _busy.Visibility = tab.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        _offline.Visibility = tab.IsOffline ? Visibility.Visible : Visibility.Collapsed;
        if (TryParseColor(tab.Accent, out var color))
        {
            _accent.Background = new SolidColorBrush(color);
            _accent.Visibility = Visibility.Visible;
        }
        else
        {
            _accent.Visibility = Visibility.Collapsed;
        }
    }

    // What a tab says when the pointer rests on it: its name (a pinned tab may show only an icon) with the marks it carries, then the full path.
    private static string TipOf(TabViewModel tab)
    {
        var marks = new List<string>(2);
        if (tab.IsPinned)
        {
            marks.Add("pinned");
        }

        if (tab.IsLocked)
        {
            marks.Add("locked");
        }

        var name = marks.Count == 0 ? tab.Title : $"{tab.Title} ({string.Join(", ", marks)})";
        var path = tab.History.Current.Value;
        return string.Equals(name, path, StringComparison.Ordinal) ? path : $"{name}{Environment.NewLine}{path}";
    }

    private void ApplyTip()
    {
        ToolTipService.SetToolTip(Item, _tip);
        ToolTipService.SetToolTip(_header, _tip);
    }

    public static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return false;
        }

        color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}

public sealed partial class MainWindow
{
    /// <summary>The colours offered by "Tab colour".</summary>
    private static readonly (string Name, string Hex)[] TabColors =
    [
        ("Orange", "#F2A33A"), ("Red", "#E2574C"), ("Blue", "#3FA9F5"), ("Green", "#4CAF7A"),
        ("Purple", "#A66BE0"), ("Pink", "#E86FA8"), ("Teal", "#26B5B0"), ("Gray", "#8C8C8C"),
    ];

    private readonly Dictionary<TabViewModel, TabItemUi> _tabUi = [];

    // Pinned tabs: the button at the end of the strip shows or hides their names (setting), and is there only while a tab is pinned.
    private async void OnPinnedNamesClick(object sender, RoutedEventArgs e)
    {
        var show = PinnedNamesButton.IsChecked == true;
        try
        {
            await App.Host.Settings.UpdateAsync(s => s with { Tabs = s.Tabs with { PinnedTabsShowNames = show } }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The change still applies for this run; the file could not be written.
            TabItemUi.ShowPinnedNames = show;
            RefreshAllTabs();
        }
    }

    private void OnTabSettingsChanged(object? sender, Leviathan.ViewModels.Settings.AppSettings settings) => OnUi(() =>
    {
        if (TabItemUi.ShowPinnedNames != settings.Tabs.PinnedTabsShowNames)
        {
            TabItemUi.ShowPinnedNames = settings.Tabs.PinnedTabsShowNames;
            RefreshAllTabs();
        }
    });

    private void RefreshAllTabs()
    {
        foreach (var (tab, ui) in _tabUi)
        {
            ui.Refresh(_viewModel.Tabs.GroupOf(tab));
        }

        UpdatePinnedNamesButton();
    }

    private void UpdatePinnedNamesButton()
    {
        var any = _viewModel.Tabs.Tabs.Any(t => t.IsPinned);
        PinnedNamesButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        PinnedNamesButton.IsChecked = TabItemUi.ShowPinnedNames;
        var text = TabItemUi.ShowPinnedNames ? "Show only the icons of pinned tabs" : "Show the names of pinned tabs";
        ToolTipService.SetToolTip(PinnedNamesButton, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PinnedNamesButton, text);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TabViewModel tab && _tabUi.TryGetValue(tab, out var ui))
        {
            OnUi(() =>
            {
                ui.Refresh(_viewModel.Tabs.GroupOf(tab));
                if (e.PropertyName == nameof(TabViewModel.IsPinned))
                {
                    UpdatePinnedNamesButton();
                }

                // Joining or leaving a group changes where the chip stands; the group's own properties go through SyncTabs.
                if (e.PropertyName == nameof(TabViewModel.GroupId))
                {
                    SyncTabs();
                }
            });
        }
    }

    // Title bar regions ------------------------------------------------------------------------------------------------

    private string _titleBarRegions = string.Empty;

    /// <summary>
    /// With the tab strip in the title bar, Windows treats the whole bar as "drag the window" unless told which parts are
    /// interactive. The tabs, the add button and the all-tabs button are declared pass-through (they get the mouse), recomputed
    /// whenever the layout changes; the empty rest of the strip stays the place to grab the window.
    /// </summary>
    private void OnTabsLayoutUpdated(object? sender, object e) => UpdateTitleBarRegions();

    private void UpdateTitleBarRegions()
    {
        if (_closed || Content.XamlRoot is not { } root)
        {
            return;
        }

        var scale = root.RasterizationScale;
        var rects = new List<RectInt32>();

        void Add(FrameworkElement? element)
        {
            if (element is null || element.ActualWidth <= 0 || element.ActualHeight <= 0 || element.Visibility != Visibility.Visible)
            {
                return;
            }

            var origin = element.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            rects.Add(new RectInt32(
                (int)Math.Floor(origin.X * scale),
                (int)Math.Floor(origin.Y * scale),
                (int)Math.Ceiling(element.ActualWidth * scale),
                (int)Math.Ceiling(element.ActualHeight * scale)));
        }

        foreach (var item in TabsView.TabItems.OfType<TabViewItem>())
        {
            Add(item);
        }

        Add(FindDescendant(TabsView, "AddButton"));
        Add(AllTabsButton);
        Add(PinnedNamesButton);

        // The whole strip is the place to grab the window, not just the small footer element (the tabs punch holes through it).
        var strip = new List<RectInt32>();
        if (TabsView.ActualWidth > 0 && TabsView.ActualHeight > 0)
        {
            strip.Add(new RectInt32(0, 0, (int)Math.Ceiling(TabsView.ActualWidth * scale), (int)Math.Ceiling(TabsView.ActualHeight * scale)));
        }

        var signature = string.Join(';', rects.Concat(strip).Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
        if (signature == _titleBarRegions)
        {
            return;
        }

        _titleBarRegions = signature;
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        source.SetRegionRects(NonClientRegionKind.Caption, [.. strip]);
        source.SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }

    private static FrameworkElement? FindDescendant(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindDescendant(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // Drag and drop (spec 4.1: reorder, tear off, merge) -------------------------------------------------------------

    // XAML event handlers must be instance methods even when they use no instance data.
#pragma warning disable CA1822
    // A drag reorders the strip's own items as the pointer moves. The strip is brought in line with the model once, when the drag is over:
    // looking at it half way (after the item left its place and before it landed) put the tab back where it started.
    private bool _tabDragging;

    // Where along the strip a tab was let go when it was close enough to the strip to count as a reorder (see OnTabDroppedOutside).
    private double? _nearDropX;

    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        _tabDragging = true;
        _nearDropX = null;
        UiTrace.Mark($"tab drag starting: {(args.Tab.Tag as TabViewModel)?.Title}");
        _draggedItem = args.Tab.Tag;
        if (args.Tab.Tag is TabViewModel tab)
        {
            App.DraggedTab = tab;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(tab.History.Current.Value);
        }
        else if (args.Tab.Tag is TabGroup group)
        {
            App.DraggedGroup = group;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(group.Name);
        }
    }

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        UiTrace.Mark($"tab drag completed: {args.DropResult}");
        App.DraggedTab = null;
        App.DraggedGroup = null;
        _tabDragging = false;

        // Wherever the strip left the tab, a tab let go over its own strip goes where the pointer is.
        _nearDropX = NativeCursor.TryGetPosition(out var x, out var y) ? NearStripX(x, y) : null;
        UiTrace.Mark($"tab drag completed; cursor {(_nearDropX is { } nx ? $"near the strip at {nx:0}" : "away from the strip")}");
        AfterDrag(FinishTabDrag);
    }

    /// <summary>The drag is over: put the tab where it was let go, then make the strip show what the model says.</summary>
    private void FinishTabDrag()
    {
        var nearX = _nearDropX;
        _nearDropX = null;
        if (nearX is { } x && _draggedItem is TabViewModel tab && _viewModel.Tabs.Tabs.Contains(tab))
        {
            // Let go near the strip, so the strip may have put the item back: the pointer decides.
            _draggedItem = null;
            var index = IndexAmongOthers(x, tab);
            UiTrace.Mark($"tab drag finished near the strip: x {x:0}, new index {index}, was {_viewModel.Tabs.Tabs.IndexOf(tab)}");
            Run(CommandIds.TabMove, tab.History.Current.Value, new TabMove(tab, index));
            SyncTabs();
            return;
        }

        UiTrace.Mark("tab drag finished: the strip's own order decides");
        ReconcileStripOrder();
    }

    /// <summary>
    /// The place a tab let go at <paramref name="x"/> takes among the other tabs: how many of them stand to its left, counting each one by
    /// the middle of its own width. The dragged tab itself is left out, wherever the strip drew it.
    /// </summary>
    private int IndexAmongOthers(double x, TabViewModel dragged)
    {
        var items = TabsView.TabItems.OfType<TabViewItem>().ToList();
        if (items.Count == 0)
        {
            return 0;
        }

        var position = items.Min(i => i.TransformToVisual(TabsView).TransformPoint(new Point(0, 0)).X);
        var index = 0;
        foreach (var item in items)
        {
            if (ReferenceEquals(item.Tag, dragged) || item.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (item.Tag is TabViewModel)
            {
                if (x < position + (item.ActualWidth / 2))
                {
                    return index;
                }

                index++;
            }

            position += item.ActualWidth;
        }

        return index;
    }
#pragma warning restore CA1822

    /// <summary>Dropped outside every strip: the tab becomes its own window at the cursor.</summary>
    private void OnTabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        var hasCursor = NativeCursor.TryGetPosition(out var x, out var y);
        UiTrace.Mark($"tab dropped outside; cursor {(hasCursor ? $"{x},{y}" : "unknown")}; tab {(args.Tab.Tag as TabViewModel)?.Title}");

        // Reordering a tab means dragging it along a strip that is only about 40 px tall, so a hand that drifts a little lands "outside". Near the
        // strip is still a reorder: the tab goes where the pointer is. Only a drop clearly away from the strip (or outside the window) tears it off.
        if (hasCursor && NearStripX(x, y) is { } stripX)
        {
            // Settled when the drag completes (FinishTabDrag), once the strip has stopped moving things around.
            _nearDropX = stripX;
            return;
        }

        if (args.Tab.Tag is TabViewModel tab && hasCursor)
        {
            // Put the new window's tab under the cursor rather than its corner.
            AfterDrag(() => Run(CommandIds.TabTearOff, tab.History.Current.Value, new TabTearOff(tab, x - 60, y - 24)));
        }
        else if (args.Tab.Tag is TabGroup group && hasCursor)
        {
            AfterDrag(() => Run(CommandIds.TabGroupTearOff, null, new TabGroupTearOff(group, x - 60, y - 24)));
        }
    }

    /// <summary>How far below the tab row, in device-independent pixels, a dragged tab may be let go and still count as a reorder.</summary>
    private const double ReorderSlack = 90;

    /// <summary>
    /// When the screen point is inside this window's width and within <see cref="ReorderSlack"/> of the tab row, the x position along the strip
    /// (in the strip's own pixels); otherwise null (the tab was dragged away on purpose).
    /// </summary>
    private double? NearStripX(int screenX, int screenY)
    {
        var scale = TabsView.XamlRoot?.RasterizationScale ?? 1.0;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var x = (screenX - position.X) / scale;
        var y = (screenY - position.Y) / scale;
        var inside = x >= 0 && x <= size.Width / scale && y >= -ReorderSlack / 2 && y <= TabsView.ActualHeight + ReorderSlack;
        return inside ? x : null;
    }

    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        // A tab or group of this very strip is the strip's own business (it reorders itself); answering "accepted" for it would tell the strip
        // that the app took the drop, and the reorder is lost. Only what comes from another window is accepted here.
        var own = (App.DraggedTab is { } tab && _viewModel.Tabs.Tabs.Contains(tab)) || (App.DraggedGroup is { } group && _viewModel.Tabs.Groups.Contains(group));
        if (!own && (App.DraggedTab is not null || App.DraggedGroup is not null))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = App.DraggedGroup is not null ? "Move group here" : "Move tab here";
        }
    }

    /// <summary>A tab from another window (or this one) dropped on the strip: it joins at the pointer.</summary>
    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        UiTrace.Mark($"tab strip drop; dragged tab: {App.DraggedTab?.Title}");
        if (App.DraggedGroup is { } group)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            var at = IndexAt(e.GetPosition(TabsView).X);
            AfterDrag(() => Run(CommandIds.TabGroupDrop, null, new TabGroupDrop(group, at)));
        }
        else if (App.DraggedTab is { } tab && _viewModel.Tabs.Tabs.Contains(tab))
        {
            // One of this strip's own tabs let go on it: that is a reorder, which the strip and FinishTabDrag settle.
            e.AcceptedOperation = DataPackageOperation.Move;
        }
        else if (App.DraggedTab is { } foreign)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            var index = IndexAt(e.GetPosition(TabsView).X);
            AfterDrag(() => Run(CommandIds.TabDrop, foreign.History.Current.Value, new TabDrop(foreign, index)));
        }
    }

    /// <summary>Runs a change to the tab strip once the drag has completely finished; changing the tabs inside the drop callback would pull the dragged tab out from under WinUI.</summary>
    private void AfterDrag(Action change) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => change());

    private int IndexAt(double x)
    {
        // The index among the tabs; chips are not tabs.
        var tabs = 0;
        foreach (var entry in TabsView.TabItems.OfType<TabViewItem>())
        {
            if (entry.Tag is not TabViewModel || entry.Visibility != Visibility.Visible)
            {
                continue;
            }

            var centre = entry.TransformToVisual(TabsView).TransformPoint(new Point(entry.ActualWidth / 2, 0)).X;
            if (x < centre)
            {
                return TabsView.TabItems.OfType<TabViewItem>().Where(i => i.Tag is TabViewModel).ToList().IndexOf(entry);
            }

            tabs++;
        }

        return _viewModel.Tabs.Tabs.Count;
    }

    /// <summary>The strip reordered itself while the user dragged within it: tell the view model, which settles the order (pinned tabs stay left).</summary>
    private void OnTabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
    {
        if (_syncingTabs || _tabDragging)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(ReconcileStripOrder);
    }

    // Context menu -----------------------------------------------------------------------------------------------------

    private MenuFlyout BuildTabMenu(TabViewModel tab)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            MenuFlyoutItem Item(string text, CommandId id, object? parameter = null, string? target = null, Action? custom = null)
            {
                var item = new MenuFlyoutItem { Text = text, IsEnabled = _commands.CanExecute(id, new CommandContext(target, parameter ?? tab)) };
                item.Click += (_, _) =>
                {
                    if (custom is not null)
                    {
                        custom();
                    }
                    else
                    {
                        Run(id, target ?? tab.History.Current.Value, parameter ?? tab);
                    }
                };
                return item;
            }

            menu.Items.Add(Item("New tab to the right", CommandIds.TabNewRight));
            menu.Items.Add(Item("New tab to the left", CommandIds.TabNewLeft));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(tab.IsPinned ? "Unpin tab" : "Pin tab", CommandIds.TabTogglePin));
            menu.Items.Add(Item(tab.IsLocked ? "Unlock tab" : "Lock tab", CommandIds.TabToggleLock));

            // A name belongs to a locked tab: an unlocked tab always shows its folder, and unlocking takes the name away.
            var rename = Item("Name this tab…", CommandIds.TabRename, custom: () => _ = RenameTabAsync(tab));
            if (!tab.IsLocked)
            {
                ToolTipService.SetToolTip(rename, "Lock the tab first. An unlocked tab always shows the name of its folder.");
            }

            menu.Items.Add(rename);

            var colour = new MenuFlyoutSubItem { Text = "Tab colour" };
            colour.Items.Add(ColourItem("None", string.Empty, null));
            foreach (var (name, hex) in TabColors)
            {
                colour.Items.Add(ColourItem(name, hex, hex));
            }

            menu.Items.Add(colour);
            menu.Items.Add(new MenuFlyoutSeparator());
            AddGroupEntries(menu, tab);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Duplicate tab", CommandIds.TabDuplicate));
            menu.Items.Add(Item("Close tab", CommandIds.TabClose));
            menu.Items.Add(Item("Close other tabs", CommandIds.TabCloseOthers));
            menu.Items.Add(Item("Close tabs to the right", CommandIds.TabCloseRight));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Reopen closed tab", CommandIds.TabReopen));

            MenuFlyoutItem ColourItem(string name, string value, string? hex)
            {
                var item = new MenuFlyoutItem { Text = name };
                if (hex is not null && TabItemUi.TryParseColor(hex, out var color))
                {
                    item.Icon = Icons.Of("swatch", 16, hex);
                }

                item.Click += (_, _) => Run(CommandIds.TabColor, value, tab);
                return item;
            }
        };
        return menu;
    }

    private async Task RenameTabAsync(TabViewModel tab)
    {
        var box = new TextBox { Text = tab.CustomTitle ?? string.Empty, PlaceholderText = tab.History.Current.Name, MaxLength = 60, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = "Name this tab",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Run(CommandIds.TabRename, box.Text, tab);
        }
    }

    // Overflow list (spec 4.1: "a drop-down list of all tabs with search") --------------------------------------------

    private void OnAllTabsClick(object sender, RoutedEventArgs e)
    {
        var search = new TextBox { PlaceholderText = "Search tabs", MinWidth = 280 };
        var list = new ListView { MaxHeight = 360, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { search, list } } };

        void Fill()
        {
            // Tabs under their group's heading (spec §4.1); a folded group's tabs are listed too, and picking one opens the group.
            var entries = new List<TabListEntry>();
            TabGroup? current = null;
            foreach (var t in _viewModel.Tabs.Search(search.Text))
            {
                var group = _viewModel.Tabs.GroupOf(t);
                if (group is not null && group != current)
                {
                    entries.Add(new TabListEntry(null, "▌ " + (group.Name.Length == 0 ? "Unnamed group" : group.Name), string.Empty));
                }

                current = group;
                entries.Add(new TabListEntry(t, (group is null ? string.Empty : "    ") + t.Title + (t.IsPinned ? "  (pinned)" : string.Empty), t.History.Current.Value));
            }

            list.ItemsSource = entries;
        }

        search.TextChanged += (_, _) => Fill();
        list.ItemTemplate = null;
        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is TabListEntry { Tab: { } picked })
            {
                _viewModel.Tabs.Select(picked);
                flyout.Hide();
            }
        };
        Fill();
        flyout.ShowAt((FrameworkElement)sender);
    }

    private sealed record TabListEntry(TabViewModel? Tab, string Title, string Path)
    {
        public override string ToString() => Path.Length == 0 ? Title : $"{Title}    {Path}";
    }

    // Middle-click opens a folder in a new background tab (spec 4.1) -----------------------------------------------------

    private void OnFileMiddleClicked(object? sender, Core.Model.FileEntry entry)
    {
        if (ViewModels.Folders.FileColumns.IsFolderLike(entry))
        {
            Run(CommandIds.TabOpenInNewTab, entry.Location.Value, KeyTranslator.IsDown(Windows.System.VirtualKey.Shift));
        }
    }

    private void OnSidebarPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint((UIElement)sender).Properties.IsMiddleButtonPressed && (e.OriginalSource as FrameworkElement)?.DataContext is ViewModels.Sidebar.SidebarNode { Location: { } where })
        {
            e.Handled = true;
            Run(CommandIds.TabOpenInNewTab, where.Value, false);
        }
    }

    /// <summary>Moves a freshly opened window to where its tab was dropped, kept on a monitor that exists.</summary>
    internal static void PlaceWindow(Window window, ViewModels.Windows.WindowViewModel viewModel)
    {
        UiTrace.Mark("PlaceWindow: reading displays");
        // Read by index: enumerating this WinRT list with LINQ (foreach/Select) failed natively with a stowed E_NOINTERFACE and
        // took the whole process down right after a tab drop.
        var areas = DisplayArea.FindAll();
        UiTrace.Mark($"PlaceWindow: {areas.Count} displays");
        var monitors = new List<ViewModels.Windows.MonitorInfo>();
        for (var i = 0; i < areas.Count; i++)
        {
            var work = areas[i].WorkArea;
            monitors.Add(new ViewModels.Windows.MonitorInfo(new ViewModels.Windows.ScreenRect(work.X, work.Y, work.Width, work.Height), areas[i].IsPrimary));
        }

        var fitted = ViewModels.Windows.BoundsFitter.Fit(viewModel.Bounds, monitors).Rect;
        UiTrace.Mark($"PlaceWindow: moving to {fitted.X},{fitted.Y} {fitted.Width}x{fitted.Height}");
        window.AppWindow.MoveAndResize(new RectInt32(fitted.X, fitted.Y, fitted.Width, fitted.Height));
        if (viewModel.Bounds.IsMaximized && window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        UiTrace.Mark("PlaceWindow: moved");
    }
}

/// <summary>The few Win32 calls the shell needs; small and blittable, so <c>LibraryImport</c> (CLAUDE.md).</summary>
internal static partial class NativeCursor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point32 point);

    /// <summary>The cursor position in screen pixels.</summary>
    public static bool TryGetPosition(out int x, out int y)
    {
        var ok = GetCursorPos(out var point);
        x = point.X;
        y = point.Y;
        return ok;
    }
}
