using System.Collections.Specialized;
using System.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// The Details view (spec §8.2): a header with sortable, resizable and reorderable columns above a virtualized list of
/// fixed-height rows. It shows a <see cref="TabViewModel"/> and writes back only through that tab's view models: header clicks
/// sort, the selection model holds the selection, the column layout holds widths and order.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The skeleton storyboard is stopped whenever the skeleton is hidden; nothing here needs disposing.")]
public sealed partial class DetailsGrid : UserControl
{
    private const double GripWidth = 8;
    private const double DragThreshold = 6;
    private const int SkeletonRows = 18;

    private readonly HashSet<GridRow> _realized = [];
    private readonly Stack<GridRow> _pool = new();
    private readonly Storyboard _pulse = new();
    private TabViewModel? _tab;
    private bool _hasFocus;
    private HeaderGesture? _gesture;
    private readonly BandDrag _band;
    private readonly ItemDragSource _dragSource;
    private Core.Paths.LocationPath? _dropFolder;

    /// <summary>Creates the grid.</summary>
    public DetailsGrid()
    {
        InitializeComponent();
        _band = new BandDrag(Scroller, Repeater, Band, vertical: true, RowsUnder);
        _dragSource = new ItemDragSource(Scroller, () => _tab?.Selection);
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;
        Repeater.ItemTemplate = new RowFactory(this);
        BuildSkeleton();
        GotFocus += (_, _) => SetFocusState(true);
        LostFocus += (_, _) => SetFocusState(false);
        KeyDown += OnGridKeyDown;
    }

    /// <summary>A row was opened: double-click or Enter.</summary>
    internal event EventHandler<FileEntry>? ItemInvoked;

    /// <summary>A header was clicked (Shift = add as a secondary sort).</summary>
    internal event EventHandler<SortRequest>? SortRequested;

    /// <summary>A row was middle-clicked (a folder opens in a new tab).</summary>
    internal event EventHandler<FileEntry>? MiddleClicked;

    /// <summary>A row or the empty area was right-clicked; the context menu arrives with the shell integration (slice F).</summary>
    internal event EventHandler<RowContextRequest>? RowContextRequested;

    /// <summary>Items were dropped on a folder row or on the empty area (the folder shown).</summary>
    internal event EventHandler<ItemDropRequest>? DropRequested;

    /// <summary>The inline rename box was closed: Enter, Tab, click-away (rename) or Esc (cancel).</summary>
    internal event EventHandler<RenameResult>? RenameFinished;

    internal void RaiseRenameFinished(RenameResult result) => RenameFinished?.Invoke(this, result);

    /// <summary>The item whose rename box is showing, or null.</summary>
    internal Core.Paths.LocationPath? Renaming => _tab?.Renaming;

    internal ColumnLayout? Layout => _tab?.Columns;

    internal SelectionModel? Selection => _tab?.Selection;

    internal FolderSizeTracker? Sizes => _tab?.Sizes;

    internal bool HasKeyboardFocus => _hasFocus;

    /// <summary>Width of a row: the columns, or the viewport if that is wider so selection spans the whole line.</summary>
    internal double RowWidth => Math.Max((Layout?.TotalWidth ?? 0) + 4, Scroller.ViewportWidth - 8);

    /// <summary>Shows <paramref name="tab"/>, or nothing for null.</summary>
    internal void Bind(TabViewModel? tab)
    {
        if (ReferenceEquals(_tab, tab))
        {
            return;
        }

        Unhook();
        _tab = tab;
        Repeater.ItemsSource = tab?.Items.Rows;
        if (tab is null)
        {
            BuildHeader();
            UpdateSkeleton();
            return;
        }

        tab.Columns.Changed += OnColumnsChanged;
        tab.Selection.Changed += OnSelectionChanged;
        tab.Sizes.Changed += OnSizesChanged;
        tab.Items.PropertyChanged += OnItemsChanged;
        tab.Items.Rows.CollectionChanged += OnRowsChanged;
        tab.Folder.PropertyChanged += OnFolderChanged;
        tab.PropertyChanged += OnTabChanged;
        BuildHeader();
        UpdateSkeleton();
        EnsureVisible();
    }

    /// <summary>Puts the keyboard focus on the grid.</summary>
    internal bool FocusGrid() => Focus(FocusState.Programmatic);

    /// <summary>Scrolls the focused row (or the first selected row) into view.</summary>
    internal void EnsureVisible()
    {
        if (_tab is null)
        {
            return;
        }

        var selected = _tab.Selection.SelectedEntries;
        var target = _tab.Selection.Focus ?? (selected.Count > 0 ? selected[0].Location : null);
        if (target is null)
        {
            return;
        }

        var rows = _tab.Items.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Location == target)
            {
                ScrollToIndex(i);
                return;
            }
        }
    }

    private void Unhook()
    {
        if (_tab is null)
        {
            return;
        }

        _tab.Columns.Changed -= OnColumnsChanged;
        _tab.Selection.Changed -= OnSelectionChanged;
        _tab.Sizes.Changed -= OnSizesChanged;
        _tab.Items.PropertyChanged -= OnItemsChanged;
        _tab.Items.Rows.CollectionChanged -= OnRowsChanged;
        _tab.Folder.PropertyChanged -= OnFolderChanged;
        _tab.PropertyChanged -= OnTabChanged;
    }

    /// <summary>The rename box opens on the row of <see cref="TabViewModel.Renaming"/> and closes when it is cleared.</summary>
    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Both views stay bound to the tab; the one that is not on screen leaves the rename box to the other.
        if (e.PropertyName != nameof(TabViewModel.Renaming) || _tab is null || (Visibility != Visibility.Visible && _tab.Renaming is not null))
        {
            return;
        }

        var hadFocus = false;
        foreach (var row in _realized.ToList())
        {
            hadFocus |= row.UpdateEditor();
        }

        if (_tab.Renaming is { } target)
        {
            var rows = _tab.Items.Rows;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Location == target)
                {
                    ScrollToIndex(i);
                    break;
                }
            }
        }
        else if (hadFocus)
        {
            FocusGrid();
        }
    }

    /// <summary>The rows whose lines touch <paramref name="area"/> (in the coordinates of the row list).</summary>
    private IEnumerable<int> RowsUnder(Windows.Foundation.Rect area)
    {
        if (_tab is null || area.Bottom < 0)
        {
            return [];
        }

        var count = _tab.Items.Rows.Count;
        var first = Math.Max(0, (int)Math.Floor(area.Top / GridRow.RowHeight));
        var last = Math.Min(count - 1, (int)Math.Floor(area.Bottom / GridRow.RowHeight));
        return first > last ? [] : Enumerable.Range(first, last - first + 1);
    }

    // Rows -------------------------------------------------------------------------------------------------------------

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is GridRow row && _tab is not null && args.Index >= 0 && args.Index < _tab.Items.Rows.Count)
        {
            _realized.Add(row);
            row.Bind(_tab.Items.Rows[args.Index]);
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is GridRow row)
        {
            _realized.Remove(row);
            row.Unbind();
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        foreach (var row in _realized)
        {
            row.UpdateVisuals();
        }

        if (_hasFocus)
        {
            EnsureVisible();
        }
    }

    private void OnSizesChanged(object? sender, IReadOnlyList<Core.Paths.LocationPath> folders)
    {
        var changed = folders.ToHashSet();
        foreach (var row in _realized.Where(r => r.Entry is { } entry && changed.Contains(entry.Location)))
        {
            row.RefreshSize();
        }
    }

    /// <summary>Takes the row height and the icon size again after the density or the icon size changed.</summary>
    internal void RefreshMetrics()
    {
        foreach (var row in _realized.ToList())
        {
            row.ApplyMetrics();
            if (row.Entry is not null)
            {
                row.Bind(row.Entry);
            }
        }

        Repeater.InvalidateMeasure();
        Repeater.InvalidateArrange();
    }

    /// <summary>Draws the icons of the rows again after the icon pack or the theme changed.</summary>
    internal void RefreshIcons()
    {
        foreach (var row in _realized.Where(r => r.Entry is not null).ToList())
        {
            row.Bind(row.Entry!);
        }
    }

    private void OnColumnsChanged(object? sender, bool structure)
    {
        if (structure)
        {
            BuildHeader();
            foreach (var row in _realized.Where(r => r.Entry is not null))
            {
                row.Bind(row.Entry!);
            }
        }
        else
        {
            ApplyHeaderWidths();
            ApplyRowWidths();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSkeleton();

    private void OnItemsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderItemsView.Sort))
        {
            ApplyHeaderSort();
        }
    }

    private void OnFolderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderViewModel.State))
        {
            UpdateSkeleton();
            if (_tab?.Folder.State == FolderLoadState.Loaded)
            {
                // After Back or Up the folder you came from is selected: bring it into view once the rows are all there.
                EnsureVisible();
            }
        }
    }

    private void ApplyRowWidths()
    {
        foreach (var row in _realized)
        {
            row.ApplyWidths();
        }
    }

    private void ScrollToIndex(int index)
    {
        var top = index * GridRow.RowHeight;
        var bottom = top + GridRow.RowHeight + 4;
        if (top < Scroller.VerticalOffset)
        {
            Scroller.ChangeView(null, top, null, true);
        }
        else if (bottom > Scroller.VerticalOffset + Scroller.ViewportHeight)
        {
            Scroller.ChangeView(null, bottom - Scroller.ViewportHeight, null, true);
        }
    }

    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        HeaderScroll.ChangeView(Scroller.HorizontalOffset, null, null, true);

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => ApplyRowWidths();

    private void SetFocusState(bool focused)
    {
        _hasFocus = focused;
        foreach (var row in _realized)
        {
            row.UpdateVisuals();
        }
    }

    // Pointer ----------------------------------------------------------------------------------------------------------

    private static GridRow? RowOf(object? source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is GridRow row)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>True when the event came from inside a rename box, which handles its own mouse and keys.</summary>
    internal static bool InEditor(object? source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is RenameBox)
            {
                return true;
            }
        }

        return false;
    }

    private void OnBodyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null || InEditor(e.OriginalSource))
        {
            return;
        }

        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(Scroller).Properties;
        if (point.IsMiddleButtonPressed)
        {
            if (RowOf(e.OriginalSource)?.Entry is { } middle)
            {
                e.Handled = true;
                MiddleClicked?.Invoke(this, middle);
            }

            return;
        }

        if (!point.IsLeftButtonPressed && !point.IsRightButtonPressed)
        {
            return;
        }

        var row = RowOf(e.OriginalSource);
        if (row?.Entry is not { } entry)
        {
            var ctrl = KeyTranslator.IsDown(VirtualKey.Control);
            // A right-click on empty space also clears it: the menu is then the folder's, as in Explorer (spec §8.5).
            if ((point.IsLeftButtonPressed || point.IsRightButtonPressed) && !ctrl && !KeyTranslator.IsDown(VirtualKey.Shift))
            {
                _tab.Selection.Clear();
            }

            // Empty space: dragging from here draws the rubber band (spec §8.4).
            if (point.IsLeftButtonPressed)
            {
                _band.Press(e, _tab.Selection, additive: ctrl);
            }

            return;
        }

        // A right-click on a selected row keeps the selection (it is the target of the menu); on another row it selects that row.
        if (point.IsRightButtonPressed && _tab.Selection.IsSelected(entry))
        {
            return;
        }

        var additive = KeyTranslator.IsDown(VirtualKey.Control);
        var range = KeyTranslator.IsDown(VirtualKey.Shift);

        // The name and icon are for drag and drop; the blank part of a row starts the rubber band (spec §8.4).
        var onName = point.IsLeftButtonPressed && row.HitsName(e.GetCurrentPoint(row).Position);
        var keepSelection = onName && _dragSource.Arm(e, row, entry, plain: !additive && !range);
        if (!keepSelection)
        {
            _tab.Selection.Click(entry, additive, range);
        }

        if (point.IsLeftButtonPressed && !onName)
        {
            _band.Press(e, _tab.Selection, additive);
        }
    }

    // Drop target ------------------------------------------------------------------------------------------------------

    /// <summary>The folder row under <paramref name="position"/> (in the coordinates of the row list), or null for a file or empty space.</summary>
    private FileEntry? FolderAt(Windows.Foundation.Point position)
    {
        if (_tab is null || position.Y < 0)
        {
            return null;
        }

        var index = (int)Math.Floor(position.Y / GridRow.RowHeight);
        var rows = _tab.Items.Rows;
        return index < rows.Count && rows[index].Kind == FileEntryKind.Folder ? rows[index] : null;
    }

    private void MarkDropFolder(Core.Paths.LocationPath? folder)
    {
        if (_dropFolder == folder)
        {
            return;
        }

        _dropFolder = folder;
        foreach (var row in _realized)
        {
            row.SetDropTarget(folder is not null && row.Entry?.Location == folder);
        }
    }

    private void OnBodyDragOver(object sender, DragEventArgs e)
    {
        if (_tab is null || !FileDrag.Accepts(e))
        {
            return;
        }

        e.Handled = true;
        var folder = FolderAt(e.GetPosition(Repeater));
        var decision = FileDrag.Over(e, folder?.Location ?? _tab.History.Current);
        MarkDropFolder(decision.Allowed ? folder?.Location : null);
    }

    private void OnBodyDragLeave(object sender, DragEventArgs e) => MarkDropFolder(null);

    private async void OnBodyDrop(object sender, DragEventArgs e)
    {
        if (_tab is null || !FileDrag.Accepts(e))
        {
            return;
        }

        e.Handled = true;
        var destination = FolderAt(e.GetPosition(Repeater))?.Location ?? _tab.History.Current;
        var modifiers = FileDrag.ModifiersOf(e);
        MarkDropFolder(null);
        var deferral = e.GetDeferral();
        try
        {
            var sources = await FileDrag.SourcesAsync(e);
            if (sources.Count > 0)
            {
                DropRequested?.Invoke(this, new ItemDropRequest(destination, sources, modifiers));
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnBodyDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!InEditor(e.OriginalSource) && RowOf(e.OriginalSource)?.Entry is { } entry)
        {
            e.Handled = true;
            ItemInvoked?.Invoke(this, entry);
        }
    }

    // Mouse, touch, pen, the menu key and Shift+F10 all arrive here; the keyboard has no pointer position, so the menu opens near the top of the list.
    private void OnBodyContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // The rename box has its own menu (cut, copy, paste text).
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        e.Handled = true;
        var target = (FrameworkElement)sender;
        var position = e.TryGetPosition(target, out var point) ? point : new Windows.Foundation.Point(48, 48);
        RowContextRequested?.Invoke(this, new RowContextRequest(null, target, position));
    }

    // Keyboard ---------------------------------------------------------------------------------------------------------

    private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_tab is null || InEditor(e.OriginalSource))
        {
            return;
        }

        var ctrl = KeyTranslator.IsDown(VirtualKey.Control);
        var shift = KeyTranslator.IsDown(VirtualKey.Shift);
        var alt = KeyTranslator.IsDown(VirtualKey.Menu);
        var page = Math.Max(1, (int)(Scroller.ViewportHeight / GridRow.RowHeight) - 1);
        int? delta = e.Key switch
        {
            VirtualKey.Up when !alt => -1,
            VirtualKey.Down when !alt => 1,
            VirtualKey.PageUp => -page,
            VirtualKey.PageDown => page,
            VirtualKey.Home => int.MinValue,
            VirtualKey.End => int.MaxValue,
            _ => null,
        };

        if (delta is { } step)
        {
            e.Handled = true;
            _tab.Selection.Move(step, shift, ctrl);
            return;
        }

        if (e.Key == VirtualKey.Enter && !alt)
        {
            var entry = _tab.Selection.Focus is { } focus ? _tab.Items.Rows.FirstOrDefault(r => r.Location == focus) : (_tab.Selection.SelectedEntries is { Count: > 0 } chosen ? chosen[0] : null);
            if (entry is not null)
            {
                e.Handled = true;
                ItemInvoked?.Invoke(this, entry);
            }

            return;
        }

        if (e.Key == VirtualKey.Space && !alt)
        {
            // Space toggles the focused row (spec §8.4).
            e.Handled = _tab.Selection.ToggleFocus();
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _tab.Selection.Clear();
            return;
        }

        if (!ctrl && !alt && CharOf(e.Key) is { } character)
        {
            // Type-ahead: the first row whose name starts with what was typed (spec §5.3).
            e.Handled = _tab.Selection.TypeAhead(character) is not null;
        }
    }

    private static char? CharOf(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => (char)('a' + (key - VirtualKey.A)),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => (char)('0' + (key - VirtualKey.Number0)),
        _ => null,
    };

    // Header -----------------------------------------------------------------------------------------------------------

    private void BuildHeader()
    {
        HeaderPanel.Children.Clear();
        if (_tab is null)
        {
            return;
        }

        foreach (var slot in _tab.Columns.Slots)
        {
            var id = slot.Column.Id;
            var cell = new Grid { Width = slot.Width, Background = new SolidColorBrush(Colors.Transparent), Tag = id };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = slot.Column.Title,
                Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = slot.RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsHitTestVisible = false,
            };
            var sort = new FontIcon { FontSize = 10, Margin = new Thickness(0, 0, GripWidth, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "sort", IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Grid.SetColumn(sort, 1);
            var grip = new ResizeGrip { Width = GripWidth, HorizontalAlignment = HorizontalAlignment.Right, Tag = id };
            Grid.SetColumnSpan(grip, 2);
            grip.PointerPressed += OnGripPressed;
            grip.PointerMoved += OnGripMoved;
            grip.PointerReleased += OnGripReleased;
            grip.PointerCanceled += OnGripReleased;
            grip.DoubleTapped += OnGripDoubleTapped;
            cell.Children.Add(title);
            cell.Children.Add(sort);
            cell.Children.Add(grip);
            cell.PointerPressed += OnHeaderPressed;
            cell.PointerMoved += OnHeaderMoved;
            cell.PointerReleased += OnHeaderReleased;
            cell.PointerCanceled += OnHeaderCanceled;
            HeaderPanel.Children.Add(cell);
        }

        ApplyHeaderSort();
        ApplyHeaderWidths();
        ApplyRowWidths();
    }

    private void ApplyHeaderWidths()
    {
        if (_tab is null)
        {
            return;
        }

        for (var i = 0; i < HeaderPanel.Children.Count && i < _tab.Columns.Slots.Count; i++)
        {
            ((FrameworkElement)HeaderPanel.Children[i]).Width = _tab.Columns.Slots[i].Width;
        }
    }

    private void ApplyHeaderSort()
    {
        if (_tab is null)
        {
            return;
        }

        var sort = _tab.Items.Sort;
        foreach (Grid cell in HeaderPanel.Children.OfType<Grid>())
        {
            var key = sort.FirstOrDefault(k => k.Column.Equals((string)cell.Tag, StringComparison.OrdinalIgnoreCase));
            if (cell.Children.OfType<FontIcon>().FirstOrDefault(i => (string?)i.Tag == "sort") is { } icon)
            {
                icon.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
                icon.Glyph = key?.Direction == SortDirection.Descending ? "" : "";
            }
        }
    }

    private void OnHeaderPressed(object sender, PointerRoutedEventArgs e)
    {
        var cell = (Grid)sender;
        if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed)
        {
            return;
        }

        cell.CapturePointer(e.Pointer);
        _gesture = new HeaderGesture((string)cell.Tag, e.GetCurrentPoint(HeaderPanel).Position.X);
    }

    private void OnHeaderMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture is not { Resizing: false } gesture || _tab is null)
        {
            return;
        }

        var x = e.GetCurrentPoint(HeaderPanel).Position.X;
        if (!gesture.Dragging && Math.Abs(x - gesture.StartX) > DragThreshold)
        {
            gesture.Dragging = true;
        }

        if (gesture.Dragging)
        {
            var index = _tab.Columns.IndexAt(x);
            var slots = _tab.Columns.Slots;
            var from = _tab.Columns.Ids.ToList().IndexOf(gesture.ColumnId);
            // The marker sits on the edge the column would land against.
            var edge = index <= from ? _tab.Columns.OffsetOf(slots[index].Column.Id) : _tab.Columns.OffsetOf(slots[index].Column.Id) + slots[index].Width;
            Canvas.SetLeft(DropMarker, edge - 1);
            DropMarker.Visibility = Visibility.Visible;
        }
    }

    private void OnHeaderReleased(object sender, PointerRoutedEventArgs e)
    {
        var cell = (Grid)sender;
        cell.ReleasePointerCapture(e.Pointer);
        DropMarker.Visibility = Visibility.Collapsed;
        if (_gesture is not { Resizing: false } gesture || _tab is null)
        {
            return;
        }

        _gesture = null;
        if (gesture.Dragging)
        {
            _tab.Columns.Move(gesture.ColumnId, _tab.Columns.IndexAt(e.GetCurrentPoint(HeaderPanel).Position.X));
        }
        else
        {
            SortRequested?.Invoke(this, new SortRequest(gesture.ColumnId, KeyTranslator.IsDown(VirtualKey.Shift)));
        }
    }

    private void OnHeaderCanceled(object sender, PointerRoutedEventArgs e)
    {
        DropMarker.Visibility = Visibility.Collapsed;
        _gesture = null;
    }

    private void OnGripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null || !e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var grip = (ResizeGrip)sender;
        var id = (string)grip.Tag;
        grip.CapturePointer(e.Pointer);
        var width = _tab.Columns.Slots.First(s => s.Column.Id == id).Width;
        _gesture = new HeaderGesture(id, e.GetCurrentPoint(HeaderPanel).Position.X) { Resizing = true, StartWidth = width };
        e.Handled = true;
    }

    private void OnGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture is { Resizing: true } gesture && _tab is not null)
        {
            _tab.Columns.Resize(gesture.ColumnId, gesture.StartWidth + e.GetCurrentPoint(HeaderPanel).Position.X - gesture.StartX);
            e.Handled = true;
        }
    }

    private void OnGripReleased(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        if (_gesture is { Resizing: true })
        {
            _gesture = null;
            e.Handled = true;
        }
    }

    private void OnGripDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_tab is not null)
        {
            AutoFit((string)((FrameworkElement)sender).Tag);
        }
    }

    /// <summary>Sizes a column to its widest visible cell (double-click on the edge of its header).</summary>
    internal void AutoFit(string columnId)
    {
        if (_tab is null || !FileColumns.TryGet(columnId, out var column))
        {
            return;
        }

        var probe = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        double Measure(string text)
        {
            probe.Text = text;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        }

        var sample = _tab.Items.Rows.Take(2000).Select(r => GridRow.TextFor(columnId, r, _tab.Sizes)).Append(column.Title);
        _tab.Columns.AutoFit(columnId, sample, Measure, columnId == "name" ? GridRow.NameExtra + 8 : 0);
    }

    /// <summary>"More columns…" was chosen in the header menu.</summary>
    internal event EventHandler? ColumnChooserRequested;

    private void OnHeaderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

        e.Handled = true;
        var menu = new MenuFlyout();
        foreach (var column in FileColumns.All)
        {
            var item = new ToggleMenuFlyoutItem { Text = column.Title, IsChecked = _tab.Columns.IsVisible(column.Id), IsEnabled = column != FileColumns.Name };
            var id = column.Id;
            item.Click += (_, _) => _tab.Columns.SetVisible(id, item.IsChecked);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var more = new MenuFlyoutItem { Text = "More columns…" };
        more.Click += (_, _) => ColumnChooserRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(more);
        var fit = new MenuFlyoutItem { Text = "Size all columns to fit" };
        fit.Click += (_, _) =>
        {
            foreach (var id in _tab.Columns.Ids.ToList())
            {
                AutoFit(id);
            }
        };
        menu.Items.Add(fit);
        menu.ShowAt(HeaderPanel, e.GetPosition(HeaderPanel));
    }

    // Skeleton rows ----------------------------------------------------------------------------------------------------

    private void BuildSkeleton()
    {
        var random = new Random(7);
        for (var i = 0; i < SkeletonRows; i++)
        {
            var row = new Grid { Height = GridRow.RowHeight, ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.Children.Add(Bar(16, 16));
            var name = Bar(120 + random.Next(0, 220), 12);
            name.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var size = Bar(50, 12);
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
            var date = Bar(100, 12);
            Grid.SetColumn(date, 3);
            row.Children.Add(date);
            Skeleton.Children.Add(row);
        }

        var pulse = new DoubleAnimation { From = 0.45, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(900)), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(pulse, Skeleton);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        _pulse.Children.Add(pulse);

        static Rectangle Bar(double width, double height) => new()
        {
            Width = width,
            Height = height,
            RadiusX = 3,
            RadiusY = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
        };
    }

    /// <summary>Skeleton rows show while a folder is loading and no row has arrived yet; they pulse gently and stop when hidden.</summary>
    private void UpdateSkeleton()
    {
        var show = _tab is { } tab && tab.Folder.State == FolderLoadState.Loading && tab.Items.Rows.Count == 0;
        if (show == (Skeleton.Visibility == Visibility.Visible))
        {
            return;
        }

        Skeleton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            _pulse.Begin();
        }
        else
        {
            _pulse.Stop();
        }
    }

    // Types ------------------------------------------------------------------------------------------------------------

    /// <summary>A header click on a column.</summary>
    internal sealed record SortRequest(string ColumnId, bool Additive);

    private sealed class HeaderGesture(string columnId, double startX)
    {
        public string ColumnId { get; } = columnId;

        public double StartX { get; } = startX;

        public double StartWidth { get; init; }

        public bool Resizing { get; init; }

        public bool Dragging { get; set; }
    }

    /// <summary>The draggable edge of a column header; shows the horizontal resize cursor.</summary>
    private sealed partial class ResizeGrip : Grid
    {
        public ResizeGrip()
        {
            Background = new SolidColorBrush(Colors.Transparent);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
    }

    /// <summary>Creates and recycles rows for the repeater.</summary>
    private sealed class RowFactory(DetailsGrid owner) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => owner._pool.Count > 0 ? owner._pool.Pop() : new GridRow(owner);

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is GridRow row)
            {
                owner._pool.Push(row);
            }
        }
    }
}
