using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.SpaceMap;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.SpaceMap;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Leviathan.App;

/// <summary>What the space map can do with an item, supplied by the window that opened it (spec §14 "Actions").</summary>
/// <param name="Open">Opens a file with its program, or a folder in a new tab.</param>
/// <param name="RevealInTab">Shows the item in a tab of the main window.</param>
/// <param name="DeleteAsync">Deletes items the way the file view does (Recycle Bin, or a share's recycle folder); returns whether it was done.</param>
/// <param name="ToggleTagAsync">Puts the colour tag with this number on the item, or takes it off.</param>
/// <param name="Tags">The tags to offer, in key order.</param>
internal sealed record SpaceMapActions(
    Func<string, Task> Open,
    Action<string> RevealInTab,
    Func<IReadOnlyList<string>, Task<bool>> DeleteAsync,
    Func<FileEntry, int, Task> ToggleTagAsync,
    Func<IReadOnlyList<string>> Tags);

/// <summary>
/// The space map (spec §14): a sortable tree of folders with sizes and shares on the left, the treemap on the right, the legend underneath. It fills in while
/// the scan runs, can be paused or stopped (a stopped scan continues next time), and a finished scan is kept so it opens at once. Click a rectangle or a row to
/// select the other; double-click to zoom in; Backspace zooms out. This is a window of its own rather than a tab: the tab strip holds folders (recorded in docs/decisions.md).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The scan and timer are stopped when the window closes (Closed), which a Window cannot express as IDisposable.")]
internal sealed class SpaceMapWindow : Window
{
    private static readonly string[] SizeFilters = ["All files", "Over 1 MB", "Over 10 MB", "Over 100 MB", "Over 1 GB"];
    private static readonly long[] SizeFilterBytes = [0, 1L << 20, 10L << 20, 100L << 20, 1L << 30];

    private readonly SpaceScanner _scanner;
    private readonly LocationPath _location;
    private readonly SpaceMapActions _actions;
    private readonly Grid _layout = new() { RowSpacing = 6, Padding = new Thickness(12), IsTabStop = true };
    private readonly Grid _mapHost = new() { MinWidth = 200, MinHeight = 120 };
    private readonly Image _map = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ListView _tree = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, Padding = new Thickness(0) };
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _up = new() { Content = "Zoom out", IsEnabled = false };
    private readonly Button _pause = new() { Content = "Pause" };
    private readonly Button _stop = new() { Content = "Stop" };
    private readonly Button _rescan = new() { Content = "Rescan" };
    private readonly ComboBox _mode = new() { MinWidth = 130 };
    private readonly ComboBox _size = new() { MinWidth = 130 };
    private readonly ToggleButton _byExtension = new() { Content = "Legend by extension" };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private SpaceScanSession _session = null!;
    private SpaceMapState _state = null!;
    private SpaceMapFrame? _frame;
    private int _building;
    private bool _dirty;
    private bool _closed;
    private IReadOnlyList<SpaceRow> _shownRows = [];

    /// <summary>Opens the window over <paramref name="location"/> and starts (or continues, or just loads) its scan.</summary>
    public SpaceMapWindow(SpaceScanner scanner, LocationPath location, SpaceMapActions actions)
    {
        _scanner = scanner;
        _location = location;
        _actions = actions;
        Title = $"Space map · {location}";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        BuildLayout();
        Content = _layout;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(900);
        _timer.Tick += (_, _) => Refresh();
        Closed += OnClosed;
        StartScan(fresh: false);
        _timer.Start();
    }

    private void BuildLayout()
    {
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var label in new[] { "Colour by kind", "Colour by age" })
        {
            _mode.Items.Add(label);
        }

        _mode.SelectedIndex = 0;
        foreach (var label in SizeFilters)
        {
            _size.Items.Add(label);
        }

        _size.SelectedIndex = 0;
        bar.Children.Add(_up);
        bar.Children.Add(_pause);
        bar.Children.Add(_stop);
        bar.Children.Add(_rescan);
        bar.Children.Add(_mode);
        bar.Children.Add(_size);
        bar.Children.Add(_byExtension);
        Grid.SetRow(bar, 0);
        _layout.Children.Add(bar);

        var body = new Grid { ColumnSpacing = 10 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tree.ItemTemplate = (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Grid ColumnSpacing='8'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='74'/><ColumnDefinition Width='44'/></Grid.ColumnDefinitions>" +
            "<StackPanel Grid.Column='0' Orientation='Vertical'><TextBlock TextTrimming='CharacterEllipsis'/><Border Height='3' HorizontalAlignment='Left' CornerRadius='1'/></StackPanel>" +
            "<TextBlock Grid.Column='1' HorizontalAlignment='Right' FontSize='12'/><TextBlock Grid.Column='2' HorizontalAlignment='Right' FontSize='12' Opacity='0.7'/></Grid></DataTemplate>");
        _tree.ItemContainerStyle = (Style)XamlReader.Load(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'><Setter Property='MinHeight' Value='0'/><Setter Property='Padding' Value='6,2,6,2'/></Style>");
        _tree.ContainerContentChanging += OnRowContent;
        _tree.ItemClick += OnRowClick;
        _tree.DoubleTapped += OnRowDoubleTapped;
        _tree.RightTapped += OnRowRightTapped;
        Grid.SetColumn(_tree, 0);
        body.Children.Add(_tree);
        _mapHost.Children.Add(_map);
        Grid.SetColumn(_mapHost, 1);
        body.Children.Add(_mapHost);
        Grid.SetRow(body, 1);
        _layout.Children.Add(body);

        var legendScroll = new ScrollViewer { Content = _legend, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto };
        Grid.SetRow(legendScroll, 2);
        _layout.Children.Add(legendScroll);
        Grid.SetRow(_status, 3);
        _layout.Children.Add(_status);

        _up.Click += (_, _) => ZoomOut();
        _pause.Click += (_, _) => TogglePause();
        _stop.Click += (_, _) => _session.Cancel();
        _rescan.Click += (_, _) => StartScan(fresh: true);
        _mode.SelectionChanged += (_, _) => Update(_state with { Mode = _mode.SelectedIndex == 1 ? ColorMode.Age : ColorMode.Type });
        _size.SelectionChanged += (_, _) => Update(_state with { MinBytes = SizeFilterBytes[Math.Max(0, _size.SelectedIndex)] });
        _byExtension.Click += (_, _) => Update(_state with { LegendByExtension = _byExtension.IsChecked == true });
        _mapHost.SizeChanged += (_, _) => Refresh();
        _map.PointerPressed += OnMapPressed;
        _map.DoubleTapped += OnMapDoubleTapped;
        _map.RightTapped += OnMapRightTapped;
        _layout.KeyDown += OnKeyDown;
        _layout.Loaded += (_, _) => _layout.Focus(FocusState.Programmatic);
    }

    private void StartScan(bool fresh)
    {
        if (_session is { } old)
        {
            old.Cancel();
        }

        var saved = fresh ? null : _scanner.Saved(_location);
        _session = _scanner.Start(_location, saved);
        _state = new SpaceMapState(_session.Root, _state?.Mode ?? ColorMode.Type, _state?.MinBytes ?? 0, null, new HashSet<SpaceNode> { _session.Root }, _state?.LegendByExtension ?? false);
        _session.Completion.ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh), TaskScheduler.Default);
        _timer?.Start();
        Refresh();
    }

    private void Update(SpaceMapState state)
    {
        _state = state;
        _up.IsEnabled = state.Zoom != _session.Root;
        Refresh();
    }

    // The frame is built off the UI thread (it holds the scan still while it reads the tree) and applied here if nothing newer was asked for.
    private void Refresh()
    {
        if (_closed || _session is null)
        {
            return;
        }

        var width = (int)_mapHost.ActualWidth;
        var height = (int)_mapHost.ActualHeight;
        if (width < 40 || height < 40)
        {
            return;
        }

        if (Interlocked.Exchange(ref _building, 1) == 1)
        {
            _dirty = true;
            return;
        }

        var state = _state;
        var session = _session;
        _ = Task.Run(() => SpaceMapFrames.Build(session, state, width, height, DateTimeOffset.UtcNow)).ContinueWith(task =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                Volatile.Write(ref _building, 0);
                if (_closed || task.IsFaulted || session != _session)
                {
                    return;
                }

                Apply(task.Result, state, width, height);
                if (_dirty)
                {
                    _dirty = false;
                    Refresh();
                }
            });
        }, TaskScheduler.Default);
    }

    private void Apply(SpaceMapFrame frame, SpaceMapState state, int width, int height)
    {
        _frame = frame;
        UiTrace.Mark($"SpaceMap frame: {frame.Tiles.Count:N0} tiles, {frame.Rows.Count:N0} rows, {frame.Status}");
        Title = frame.Title;
        var dark = ((FrameworkElement)Content).ActualTheme == ElementTheme.Dark;
        var selected = state.Selected is null ? null : SpaceMapFrames.TileOf(frame, state.Selected);
        using (UiTrace.Begin("SpaceMap.Render"))
        {
            _map.Source = TreemapBitmap.Render(frame, state.Mode, width, height, dark, selected);
        }

        using (UiTrace.Begin("SpaceMap.Rows"))
        {
            if (!frame.Rows.SequenceEqual(_shownRows))
            {
                _shownRows = frame.Rows;
                _tree.ItemsSource = frame.Rows;
            }
        }

        _legend.Children.Clear();
        foreach (var entry in frame.Legend)
        {
            var (r, g, b) = TreemapBitmap.ColourOf(state.Mode, entry.Category);
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 14, 2) };
            item.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b)), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = $"{entry.Label}  {DisplayFormat.Size(entry.Bytes)}", FontSize = 12 });
            _legend.Children.Add(item);
        }

        var running = frame.Progress is { IsComplete: false } && !_session.Completion.IsCompleted;
        _pause.Content = _session.IsPaused ? "Resume" : "Pause";
        _pause.IsEnabled = !_session.Completion.IsCompleted;
        _stop.IsEnabled = running;
        _status.Text = _session.Completion.IsCanceled ? "Stopped. What was found is saved, so opening this map again continues the scan.  |  " + frame.Status : frame.Status;
        if (_session.Completion.IsCompleted)
        {
            // Finished or stopped: no more changes are coming, so the timer rests until the user does something.
            _timer.Stop();
        }
    }

    private void OnRowContent(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not SpaceRow row || args.ItemContainer.ContentTemplateRoot is not Grid grid)
        {
            return;
        }

        var left = (StackPanel)grid.Children[0];
        var name = (TextBlock)left.Children[0];
        var bar = (Border)left.Children[1];
        var mark = row.IsFolder ? (row.IsExpanded ? "▾ " : "▸ ") : "   ";
        name.Text = mark + row.Name + (row.Note.Length > 0 ? $"  ({row.Note})" : string.Empty);
        name.FontWeight = row.IsSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        left.Margin = new Thickness(row.Depth * 16, 0, 0, 0);
        bar.Width = Math.Max(1, Math.Min(240, 240 * row.Fraction));
        bar.Background = new SolidColorBrush(row.IsFolder ? ColorHelper.FromArgb(255, 0x3B, 0x82, 0xF6) : ColorHelper.FromArgb(255, 0x94, 0xA3, 0xB8));
        ((TextBlock)grid.Children[1]).Text = row.SizeText;
        ((TextBlock)grid.Children[2]).Text = row.Fraction >= 0.01 ? $"{row.Fraction:0%}" : "<1%";
        ToolTipService.SetToolTip(grid, row.Node.FullPath + (row.IsFolder ? $"  ·  {row.FilesText} files  ·  changed {row.ModifiedText}" : $"  ·  changed {row.ModifiedText}"));
        args.ItemContainer.IsSelected = row.IsSelected;
    }

    private void OnRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SpaceRow row)
        {
            Select(row.Node);
        }
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (((FrameworkElement)e.OriginalSource).DataContext is SpaceRow { IsFolder: true } row)
        {
            var expanded = new HashSet<SpaceNode>(_state.Expanded);
            if (!expanded.Remove(row.Node))
            {
                expanded.Add(row.Node);
            }

            Update(_state with { Expanded = expanded, Selected = row.Node });
        }
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (((FrameworkElement)e.OriginalSource).DataContext is SpaceRow { } row)
        {
            Select(row.Node);
            ShowMenu(row.Node, (FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        }
    }

    private void OnMapPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_map);
        if (_frame is null || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (SpaceMapFrames.NodeAt(_frame, point.Position.X, point.Position.Y) is { } node)
        {
            Select(node);
        }
    }

    private void OnMapDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(_map);
        if (_frame is not null && SpaceMapFrames.NodeAt(_frame, point.X, point.Y) is { } node)
        {
            // A rectangle is a file: zoom into the folder that holds it. The block that stands for a folder's small items already names that folder.
            ZoomTo(node.IsFolder ? node : node.Parent ?? node);
        }
    }

    private void OnMapRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(_map);
        if (_frame is not null && SpaceMapFrames.NodeAt(_frame, point.X, point.Y) is { } node)
        {
            Select(node);
            ShowMenu(node, _map, point);
        }
    }

    private void Select(SpaceNode node)
    {
        // Selecting something deep opens the folders above it in the tree, so the row can be seen.
        var expanded = new HashSet<SpaceNode>(_state.Expanded);
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            expanded.Add(parent);
        }

        Update(_state with { Selected = node, Expanded = expanded });
    }

    private void ZoomTo(SpaceNode folder)
    {
        if (!folder.IsFolder || !_session.Root.Contains(folder))
        {
            return;
        }

        Update(_state with { Zoom = folder, Selected = null });
    }

    private void ZoomOut()
    {
        if (_state.Zoom.Parent is { } parent)
        {
            Update(_state with { Zoom = parent });
        }
    }

    private void TogglePause()
    {
        if (_session.IsPaused)
        {
            _session.Resume();
            _timer.Start();
        }
        else
        {
            _session.Pause();
        }

        Refresh();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Back)
        {
            e.Handled = true;
            ZoomOut();
        }
        else if (e.Key == VirtualKey.Delete && _state.Selected is { } node && node != _session.Root)
        {
            e.Handled = true;
            _ = DeleteAsync(node);
        }
        else if (e.Key == VirtualKey.Enter && _state.Selected is { } open)
        {
            e.Handled = true;
            _ = _actions.Open(open.FullPath);
        }
    }

    private void ShowMenu(SpaceNode node, FrameworkElement anchor, Windows.Foundation.Point at)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open", () => _ = _actions.Open(node.FullPath)));
        menu.Items.Add(Item("Show in a tab", () => _actions.RevealInTab(node.FullPath)));
        if (node.IsFolder)
        {
            menu.Items.Add(Item("Zoom into this folder", () => ZoomTo(node)));
        }

        var tags = _actions.Tags();
        if (!node.IsFolder && tags.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = "Tag" };
            for (var i = 0; i < Math.Min(9, tags.Count); i++)
            {
                var number = i + 1;
                sub.Items.Add(Item(tags[i], () => _ = _actions.ToggleTagAsync(ToEntry(node), number)));
            }

            menu.Items.Add(sub);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Delete…", () => _ = DeleteAsync(node)));
        menu.ShowAt(anchor, new FlyoutShowOptions { Position = at });
    }

    private static MenuFlyoutItem Item(string text, Action click)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => click();
        return item;
    }

    private static FileEntry ToEntry(SpaceNode node) => new(
        node.Name,
        LocationPath.Parse(node.FullPath),
        node.IsFolder ? FileEntryKind.Folder : FileEntryKind.File,
        node.IsFolder ? 0 : node.Size,
        node.Modified,
        node.Modified,
        node.IsFolder ? FileAttributes.Directory : FileAttributes.Normal);

    private async Task DeleteAsync(SpaceNode node)
    {
        var what = node.IsFolder ? $"the folder \"{node.Name}\" and everything in it" : $"\"{node.Name}\"";
        var dialog = new ContentDialog
        {
            Title = "Delete?",
            Content = $"Delete {what}? It goes to the Recycle Bin, or to the share's recycle folder on a network drive, so it can be put back.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (await _actions.DeleteAsync([node.FullPath]) && node.Parent is { } parent)
        {
            // The scan is told so the map and the totals drop it at once, without scanning again.
            _session.Remove(parent, node);
            Update(_state with { Selected = null });
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _timer.Stop();
        _session.Cancel();
    }
}
