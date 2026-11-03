using System.Collections.Specialized;
using System.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// The List and Thumbnails layouts (spec §8.1) over the same rows and selection as the Details grid. Items are fixed-size cells
/// in a virtualizing <c>UniformGridLayout</c>: List fills columns top to bottom and scrolls sideways, Thumbnails fills rows and
/// scrolls down. Ctrl+wheel resizes thumbnails between 64 and 512 px.
/// </summary>
public sealed partial class FlowView : UserControl
{
    private readonly HashSet<FlowCell> _realized = [];
    private readonly Stack<FlowCell> _pool = new();
    private TabViewModel? _tab;
    private readonly BandDrag _band;
    private readonly ItemDragSource _dragSource;
    private Core.Paths.LocationPath? _dropFolder;
    private bool _hasFocus;

    /// <summary>Creates the view.</summary>
    public FlowView()
    {
        InitializeComponent();
        _band = new BandDrag(Scroller, Repeater, Band, vertical: true, ItemsUnder);
        _dragSource = new ItemDragSource(Scroller, () => _tab?.Selection);
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;
        Repeater.ItemTemplate = new CellFactory(this);
        Repeater.PointerWheelChanged += OnWheel;
        GotFocus += (_, _) => SetFocusState(true);
        LostFocus += (_, _) => SetFocusState(false);
        KeyDown += OnGridKeyDown;

        // When the mouse button comes up the framework can move the focus to the window's root scroll viewer, where arrow
        // keys never reach this view; take it back after it has done so.
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => ReclaimFocus()), handledEventsToo: true);
        AddHandler(TappedEvent, new TappedEventHandler((_, _) => ReclaimFocus()), handledEventsToo: true);
    }

    private void ReclaimFocus() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Focus(FocusState.Pointer));

    /// <summary>An item was opened: double-click or Enter.</summary>
    internal event EventHandler<FileEntry>? ItemInvoked;

    /// <summary>An item was middle-clicked (a folder opens in a new tab).</summary>
    internal event EventHandler<FileEntry>? MiddleClicked;

    /// <summary>An item or the empty area was right-clicked; the context menu arrives with the shell integration (slice F).</summary>
    internal event EventHandler<FileEntry?>? RowContextRequested;

    /// <summary>Ctrl+wheel in Thumbnails: positive notches make thumbnails bigger.</summary>
    internal event EventHandler<int>? ZoomRequested;

    /// <summary>Items were dropped on a folder or on the empty area (the folder shown).</summary>
    internal event EventHandler<ItemDropRequest>? DropRequested;

    /// <summary>The inline rename box was closed: Enter, Tab, click-away (rename) or Esc (cancel).</summary>
    internal event EventHandler<RenameResult>? RenameFinished;

    internal void RaiseRenameFinished(RenameResult result) => RenameFinished?.Invoke(this, result);

    /// <summary>The item whose rename box is showing, or null.</summary>
    internal Core.Paths.LocationPath? Renaming => _tab?.Renaming;

    internal SelectionModel? Selection => _tab?.Selection;

    internal bool HasKeyboardFocus => _hasFocus;

    internal static IThumbnailCache Thumbnails => App.Host.Thumbnails;

    private ViewLayout Mode => _tab?.Layout ?? ViewLayout.List;

    private int Size => _tab?.ThumbnailSize ?? ItemFlow.DefaultThumbnailSize;

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
            return;
        }

        tab.PropertyChanged += OnTabChanged;
        tab.Selection.Changed += OnSelectionChanged;
        tab.Folder.PropertyChanged += OnFolderChanged;
        Configure();
        EnsureVisible();
    }

    /// <summary>The items whose cells touch <paramref name="area"/> (in the coordinates of the item area).</summary>
    private IEnumerable<int> ItemsUnder(Windows.Foundation.Rect area)
    {
        if (_tab is null)
        {
            return [];
        }

        var list = Mode == ViewLayout.List;
        return ItemFlow.IndexesIn(
            area.Left, area.Top, area.Right, area.Bottom, _tab.Items.Rows.Count,
            list ? ItemFlow.ListItemWidth : ItemFlow.CellWidth(Size),
            list ? ItemFlow.ListItemHeight : ItemFlow.CellHeight(Size),
            ItemsPerLine, list);
    }

    /// <summary>The rename box opens on the cell of <see cref="TabViewModel.Renaming"/> and closes when it is cleared.</summary>
    private void OnRenamingChanged()
    {
        // Both views stay bound to the tab; only the one on screen has anything to show or scroll (a collapsed repeater cannot
        // create elements: crash signature 182fdb45375a).
        if (_tab is null || Visibility != Visibility.Visible)
        {
            return;
        }

        var hadFocus = false;
        foreach (var cell in _realized.ToList())
        {
            hadFocus |= cell.UpdateEditor();
        }

        if (_tab.Renaming is { } target)
        {
            var index = IndexOf(target);
            if (index >= 0)
            {
                Repeater.GetOrCreateElement(index).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
        }
        else if (hadFocus)
        {
            FocusGrid();
        }
    }

    /// <summary>Puts the keyboard focus on the view.</summary>
    internal bool FocusGrid() => Focus(FocusState.Programmatic);

    /// <summary>Scrolls the focused item (or the first selected one) into view.</summary>
    internal void EnsureVisible()
    {
        // Both views stay bound to every tab; a collapsed repeater cannot create elements (the same COMException as crash signature
        // 182fdb45375a, on a tab switch with a selection). MainWindow.ApplyLayout scrolls when this view is shown.
        if (_tab is null || Visibility != Visibility.Visible)
        {
            return;
        }

        var selected = _tab.Selection.SelectedEntries;
        var target = _tab.Selection.Focus ?? (selected.Count > 0 ? selected[0].Location : null);
        if (target is null)
        {
            return;
        }

        var index = IndexOf(target);
        if (index >= 0 && index < _tab.Items.Rows.Count)
        {
            Repeater.GetOrCreateElement(index).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private void Unhook()
    {
        if (_tab is null)
        {
            return;
        }

        _tab.PropertyChanged -= OnTabChanged;
        _tab.Selection.Changed -= OnSelectionChanged;
        _tab.Folder.PropertyChanged -= OnFolderChanged;
    }

    private int IndexOf(Core.Paths.LocationPath location)
    {
        var rows = _tab!.Items.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Location == location)
            {
                return i;
            }
        }

        return -1;
    }

    // Layout -----------------------------------------------------------------------------------------------------------

    /// <summary>Applies the layout and thumbnail size to the repeater and to the cells already on screen.</summary>
    private void Configure()
    {
        if (_tab is null)
        {
            return;
        }

        var list = Mode == ViewLayout.List;
        _band.Vertical = !list;
        Scroller.HorizontalScrollBarVisibility = list ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Scroller.HorizontalScrollMode = list ? ScrollMode.Auto : ScrollMode.Disabled;
        Scroller.VerticalScrollBarVisibility = list ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        Scroller.VerticalScrollMode = list ? ScrollMode.Disabled : ScrollMode.Auto;
        Repeater.Layout = new UniformGridLayout
        {
            Orientation = list ? Orientation.Vertical : Orientation.Horizontal,
            MinItemWidth = list ? ItemFlow.ListItemWidth : ItemFlow.CellWidth(Size),
            MinItemHeight = list ? ItemFlow.ListItemHeight : ItemFlow.CellHeight(Size),
            MinRowSpacing = ItemFlow.Spacing,
            MinColumnSpacing = ItemFlow.Spacing,
            ItemsStretch = UniformGridLayoutItemsStretch.None,
        };

        foreach (var cell in _realized.Where(c => c.Entry is not null).ToList())
        {
            cell.Bind(cell.Entry!, Mode, Size);
        }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Layout) or nameof(TabViewModel.ThumbnailSize))
        {
            Configure();
            EnsureVisible();
        }
        else if (e.PropertyName == nameof(TabViewModel.Renaming))
        {
            OnRenamingChanged();
        }
    }

    private int ItemsPerLine => Mode == ViewLayout.List
        ? ItemFlow.PerLine(Scroller.ViewportHeight - 8, ItemFlow.ListItemHeight)
        : ItemFlow.PerLine(Math.Max(Repeater.ActualWidth, Scroller.ViewportWidth - 8), ItemFlow.CellWidth(Size));

    private int VisibleLines => Mode == ViewLayout.List
        ? Math.Max(1, (int)(Scroller.ViewportWidth / (ItemFlow.ListItemWidth + ItemFlow.Spacing)))
        : Math.Max(1, (int)(Scroller.ViewportHeight / (ItemFlow.CellHeight(Size) + ItemFlow.Spacing)));

    // Items ------------------------------------------------------------------------------------------------------------

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FlowCell cell && _tab is not null && args.Index >= 0 && args.Index < _tab.Items.Rows.Count)
        {
            _realized.Add(cell);
            cell.Bind(_tab.Items.Rows[args.Index], Mode, Size);
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FlowCell cell)
        {
            _realized.Remove(cell);
            cell.Unbind();
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        foreach (var cell in _realized)
        {
            cell.UpdateVisuals();
        }

        if (_hasFocus)
        {
            EnsureVisible();
        }
    }

    private void OnFolderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderViewModel.State) && _tab?.Folder.State == FolderLoadState.Loaded)
        {
            // After Back or Up the folder you came from is selected: bring it into view once the rows are all there.
            EnsureVisible();
        }
    }

    private void SetFocusState(bool focused)
    {
        _hasFocus = focused;
        foreach (var cell in _realized)
        {
            cell.UpdateVisuals();
        }
    }

    // Pointer ----------------------------------------------------------------------------------------------------------

    private static FlowCell? CellOf(object? source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FlowCell cell)
            {
                return cell;
            }
        }

        return null;
    }

    private void OnBodyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tab is null || DetailsGrid.InEditor(e.OriginalSource))
        {
            return;
        }

        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(Scroller).Properties;
        if (point.IsMiddleButtonPressed)
        {
            if (CellOf(e.OriginalSource)?.Entry is { } middle)
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

        if (CellOf(e.OriginalSource)?.Entry is not { } entry)
        {
            var ctrl = KeyTranslator.IsDown(VirtualKey.Control);
            if (point.IsLeftButtonPressed && !ctrl && !KeyTranslator.IsDown(VirtualKey.Shift))
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

        // A right-click on a selected item keeps the selection (it is the target of the menu).
        if (point.IsRightButtonPressed && _tab.Selection.IsSelected(entry))
        {
            return;
        }

        var additive = KeyTranslator.IsDown(VirtualKey.Control);
        var range = KeyTranslator.IsDown(VirtualKey.Shift);

        // Dragging from an item that is part of a larger selection takes the whole selection along.
        var cell = CellOf(e.OriginalSource)!;
        if (!(point.IsLeftButtonPressed && _dragSource.Arm(e, cell, entry, plain: !additive && !range)))
        {
            _tab.Selection.Click(entry, additive, range);
        }
    }

    // Drop target ------------------------------------------------------------------------------------------------------

    /// <summary>The folder under <paramref name="position"/> (in the coordinates of the item area), or null for a file or empty space.</summary>
    private FileEntry? FolderAt(Windows.Foundation.Point position)
    {
        if (_tab is null)
        {
            return null;
        }

        var index = ItemsUnder(new Windows.Foundation.Rect(position.X, position.Y, 1, 1)).DefaultIfEmpty(-1).First();
        var rows = _tab.Items.Rows;
        return index >= 0 && index < rows.Count && rows[index].Kind == FileEntryKind.Folder ? rows[index] : null;
    }

    private void MarkDropFolder(Core.Paths.LocationPath? folder)
    {
        if (_dropFolder == folder)
        {
            return;
        }

        _dropFolder = folder;
        foreach (var cell in _realized)
        {
            cell.SetDropTarget(folder is not null && cell.Entry?.Location == folder);
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
        if (!DetailsGrid.InEditor(e.OriginalSource) && CellOf(e.OriginalSource)?.Entry is { } entry)
        {
            e.Handled = true;
            ItemInvoked?.Invoke(this, entry);
        }
    }

    private void OnBodyRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (DetailsGrid.InEditor(e.OriginalSource))
        {
            return;
        }

        e.Handled = true;
        RowContextRequested?.Invoke(this, CellOf(e.OriginalSource)?.Entry);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (Mode != ViewLayout.Thumbnails || !KeyTranslator.IsDown(VirtualKey.Control))
        {
            return;
        }

        // 120 is one notch; high-resolution wheels send smaller steps, which still count as one.
        var delta = e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            e.Handled = true;
            ZoomRequested?.Invoke(this, delta > 0 ? 1 : -1);
        }
    }

    // Keyboard ---------------------------------------------------------------------------------------------------------

    private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_tab is null || DetailsGrid.InEditor(e.OriginalSource))
        {
            return;
        }

        var ctrl = KeyTranslator.IsDown(VirtualKey.Control);
        var shift = KeyTranslator.IsDown(VirtualKey.Shift);
        var alt = KeyTranslator.IsDown(VirtualKey.Menu);
        var rows = _tab.Items.Rows;
        var current = _tab.Selection.Focus is { } focus ? IndexOf(focus) : -1;
        var columnMajor = ItemFlow.IsColumnMajor(Mode);
        int? target = null;
        FlowKey? arrow = e.Key switch
        {
            VirtualKey.Left when !alt => FlowKey.Left,
            VirtualKey.Right when !alt => FlowKey.Right,
            VirtualKey.Up when !alt => FlowKey.Up,
            VirtualKey.Down when !alt => FlowKey.Down,
            _ => null,
        };

        if (arrow is { } key)
        {
            target = ItemFlow.Neighbour(current, rows.Count, key, ItemsPerLine, columnMajor);
        }
        else if (e.Key == VirtualKey.PageUp || e.Key == VirtualKey.PageDown)
        {
            var delta = ItemFlow.PageDelta(VisibleLines, ItemsPerLine) * (e.Key == VirtualKey.PageUp ? -1 : 1);
            target = rows.Count == 0 ? -1 : Math.Clamp((current < 0 ? 0 : current) + delta, 0, rows.Count - 1);
        }
        else if (e.Key == VirtualKey.Home)
        {
            target = rows.Count == 0 ? -1 : 0;
        }
        else if (e.Key == VirtualKey.End)
        {
            target = rows.Count - 1;
        }

        if (target is { } index)
        {
            e.Handled = true;
            if (index >= 0)
            {
                // The selection model moves by distance from the focus; with no focus it starts at the item we chose.
                if (current < 0)
                {
                    _tab.Selection.Click(rows[index], ctrl, shift);
                }
                else
                {
                    _tab.Selection.Move(index - current, shift, ctrl);
                }
            }

            return;
        }

        if (e.Key == VirtualKey.Enter && !alt)
        {
            var entry = current >= 0 ? rows[current] : (_tab.Selection.SelectedEntries is { Count: > 0 } chosen ? chosen[0] : null);
            if (entry is not null)
            {
                e.Handled = true;
                ItemInvoked?.Invoke(this, entry);
            }

            return;
        }

        if (e.Key == VirtualKey.Space && !alt)
        {
            // Space toggles the focused item (spec §8.4).
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
            // Type-ahead: the first item whose name starts with what was typed (spec §5.3).
            e.Handled = _tab.Selection.TypeAhead(character) is not null;
        }
    }

    private static char? CharOf(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => (char)('a' + (key - VirtualKey.A)),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => (char)('0' + (key - VirtualKey.Number0)),
        _ => null,
    };

    // Types ------------------------------------------------------------------------------------------------------------

    /// <summary>Creates and recycles cells for the repeater.</summary>
    private sealed class CellFactory(FlowView owner) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => owner._pool.Count > 0 ? owner._pool.Pop() : new FlowCell(owner);

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is FlowCell cell)
            {
                owner._pool.Push(cell);
            }
        }
    }
}
