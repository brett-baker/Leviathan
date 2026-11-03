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
    private readonly FontIcon _lock = new() { Glyph = "", FontSize = 11, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _busy = new() { Width = 12, Height = 12, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _offline = new() { Glyph = "", FontSize = 12, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _accent = new() { Height = 2, CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };

    public TabItemUi(TabViewModel tab)
    {
        Tab = tab;
        _offline.Foreground = new SolidColorBrush(Colors.DarkOrange);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(_lock);
        content.Children.Add(_title);
        content.Children.Add(_busy);
        content.Children.Add(_offline);
        var header = new Grid();
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.Children.Add(content);
        Grid.SetRow(_accent, 1);
        header.Children.Add(_accent);
        Item = new TabViewItem { Tag = tab, Header = header, IconSource = new SymbolIconSource { Symbol = Symbol.Folder } };
        Refresh();
    }

    public TabViewModel Tab { get; }

    public TabViewItem Item { get; }

    public void Refresh()
    {
        var tab = Tab;
        _title.Text = tab.Title;
        _title.Visibility = tab.IsPinned ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(Item, tab.History.Current.Value);
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

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TabViewModel tab && _tabUi.TryGetValue(tab, out var ui))
        {
            OnUi(ui.Refresh);
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
    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        UiTrace.Mark($"tab drag starting: {(args.Tab.Tag as TabViewModel)?.Title}");
        if (args.Tab.Tag is TabViewModel tab)
        {
            App.DraggedTab = tab;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(tab.History.Current.Value);
        }
    }

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        UiTrace.Mark($"tab drag completed: {args.DropResult}");
        App.DraggedTab = null;
    }
#pragma warning restore CA1822

    /// <summary>Dropped outside every strip: the tab becomes its own window at the cursor.</summary>
    private void OnTabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        var hasCursor = NativeCursor.TryGetPosition(out var x, out var y);
        UiTrace.Mark($"tab dropped outside; cursor {(hasCursor ? $"{x},{y}" : "unknown")}; tab {(args.Tab.Tag as TabViewModel)?.Title}");
        if (args.Tab.Tag is TabViewModel tab && hasCursor)
        {
            // Put the new window's tab under the cursor rather than its corner.
            AfterDrag(() => Run(CommandIds.TabTearOff, tab.History.Current.Value, new TabTearOff(tab, x - 60, y - 24)));
        }
    }

    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        if (App.DraggedTab is not null)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "Move tab here";
        }
    }

    /// <summary>A tab from another window (or this one) dropped on the strip: it joins at the pointer.</summary>
    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        UiTrace.Mark($"tab strip drop; dragged tab: {App.DraggedTab?.Title}");
        if (App.DraggedTab is { } tab)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            var index = IndexAt(e.GetPosition(TabsView).X);
            AfterDrag(() => Run(CommandIds.TabDrop, tab.History.Current.Value, new TabDrop(tab, index)));
        }
    }

    /// <summary>Runs a change to the tab strip once the drag has completely finished; changing the tabs inside the drop callback would pull the dragged tab out from under WinUI.</summary>
    private void AfterDrag(Action change) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => change());

    private int IndexAt(double x)
    {
        for (var i = 0; i < TabsView.TabItems.Count; i++)
        {
            if (TabsView.TabItems[i] is TabViewItem item)
            {
                var centre = item.TransformToVisual(TabsView).TransformPoint(new Point(item.ActualWidth / 2, 0)).X;
                if (x < centre)
                {
                    return i;
                }
            }
        }

        return TabsView.TabItems.Count;
    }

    /// <summary>The strip reordered itself while the user dragged within it: tell the view model, which settles the order (pinned tabs stay left).</summary>
    private void OnTabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
    {
        if (_syncingTabs)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            var shown = TabsView.TabItems.OfType<TabViewItem>().Select(i => i.Tag).OfType<TabViewModel>().ToList();
            var model = _viewModel.Tabs.Tabs;
            if (shown.Count != model.Count)
            {
                SyncTabs();
                return;
            }

            for (var i = 0; i < shown.Count; i++)
            {
                if (!ReferenceEquals(shown[i], model[i]))
                {
                    Run(CommandIds.TabMove, shown[i].History.Current.Value, new TabMove(shown[i], i));
                    break;
                }
            }

            SyncTabs();
        });
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

            menu.Items.Add(Item(tab.IsPinned ? "Unpin tab" : "Pin tab", CommandIds.TabTogglePin));
            menu.Items.Add(Item(tab.IsLocked ? "Unlock tab" : "Lock tab", CommandIds.TabToggleLock));
            menu.Items.Add(Item("Rename tab…", CommandIds.TabRename, custom: () => _ = RenameTabAsync(tab)));

            var colour = new MenuFlyoutSubItem { Text = "Tab colour" };
            colour.Items.Add(ColourItem("None", string.Empty, null));
            foreach (var (name, hex) in TabColors)
            {
                colour.Items.Add(ColourItem(name, hex, hex));
            }

            menu.Items.Add(colour);
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
                    item.Icon = new FontIcon { Glyph = "", Foreground = new SolidColorBrush(color) };
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
            Title = "Rename tab",
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

        void Fill() => list.ItemsSource = _viewModel.Tabs.Search(search.Text)
            .Select(t => new TabListEntry(t, t.Title + (t.IsPinned ? "  (pinned)" : string.Empty), t.History.Current.Value))
            .ToList();

        search.TextChanged += (_, _) => Fill();
        list.ItemTemplate = null;
        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is TabListEntry entry)
            {
                _viewModel.Tabs.Select(entry.Tab);
                flyout.Hide();
            }
        };
        Fill();
        flyout.ShowAt((FrameworkElement)sender);
    }

    private sealed record TabListEntry(TabViewModel Tab, string Title, string Path)
    {
        public override string ToString() => $"{Title}    {Path}";
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
