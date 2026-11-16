using System.Numerics;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Leviathan.App;

/// <summary>
/// Dragging a tab or a group chip in the strip (spec §4.1). WinUI's own tab drag is the system's drag-and-drop, whose events arrive in an order
/// and with a timing this window cannot see or steer; reordering built on it kept failing. So the strip tracks the mouse itself: press, move and
/// release on the item with the pointer captured. While it moves, <see cref="TabDragPlanner"/> (pure, unit-tested) says where the item would land;
/// the other tabs slide aside to show it, and the item changes colour when letting go would tear it off. On release the same plan is carried out.
/// Every step is written to <see cref="TabDragTrace"/>.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Pixels the pointer must travel from the press before a click becomes a drag.</summary>
    private const double DragThreshold = 5;

    private sealed class DragSession(TabViewItem item, StripEntry entry, uint pointerId, Point start, double grabOffset)
    {
        public TabViewItem Item { get; } = item;

        public StripEntry Entry { get; } = entry;

        public uint PointerId { get; } = pointerId;

        public Point Start { get; } = start;

        /// <summary>How far right of the item's left edge the pointer was when it was pressed.</summary>
        public double GrabOffset { get; } = grabOffset;

        public bool Active { get; set; }

        public List<DragSlot> Slots { get; } = [];

        public Dictionary<StripEntry, TabViewItem> Items { get; } = [];

        /// <summary>The items that follow the pointer: the item itself, and for a chip the tabs of its group.</summary>
        public List<TabViewItem> Unit { get; } = [];

        public double Left { get; set; }

        public double Width { get; set; }

        public string LastSignature { get; set; } = string.Empty;
    }

    private DragSession? _drag;

    /// <summary>Makes a strip item (a tab or a chip) draggable.</summary>
    private void AttachTabDrag(TabViewItem item)
    {
        item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTabPointerPressed), true);
        item.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnTabPointerMoved), true);
        item.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTabPointerReleased), true);
        item.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnTabPointerLost), true);
        item.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnTabPointerLost), true);
    }

    private static StripEntry? EntryOf(TabViewItem item) => item.Tag switch
    {
        TabViewModel tab => new TabEntry(tab),
        TabGroup group => new ChipEntry(group),
        _ => null,
    };

    private static bool IsOnButton(DependencyObject? source, DependencyObject item)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, item); node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OnTabPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var item = (TabViewItem)sender;
        if (_drag is not null || !e.GetCurrentPoint(item).Properties.IsLeftButtonPressed || IsOnButton(e.OriginalSource as DependencyObject, item))
        {
            return;
        }

        if (EntryOf(item) is not { } entry)
        {
            return;
        }

        if (!TabDragPlanner.CanDrag(entry))
        {
            TabDragTrace.Log($"PRESS on {Describe(entry)}: pinned, cannot be dragged");
            return;
        }

        var point = e.GetCurrentPoint(TabsView).Position;
        var left = item.TransformToVisual(TabsView).TransformPoint(new Point(0, 0)).X;
        _drag = new DragSession(item, entry, e.Pointer.PointerId, point, point.X - left);
        item.CapturePointer(e.Pointer);
        TabDragTrace.Log($"PRESS on {Describe(entry)} at x {point.X:0}, item left {left:0}; model order {ModelOrder()}");
    }

    private void OnTabPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(TabsView).Position;
        if (!drag.Active)
        {
            if (Math.Abs(point.X - drag.Start.X) < DragThreshold && Math.Abs(point.Y - drag.Start.Y) < DragThreshold)
            {
                return;
            }

            BeginTabDrag(drag);
        }

        var plan = ComputePlan(drag, point);
        ShowPlan(drag, plan, point);
    }

    private void OnTabPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId)
        {
            return;
        }

        _drag = null;
        drag.Item.ReleasePointerCapture(e.Pointer);
        if (!drag.Active)
        {
            TabDragTrace.Log($"RELEASE of {Describe(drag.Entry)} without a drag: a click");
            return;
        }

        var point = e.GetCurrentPoint(TabsView).Position;
        var plan = ComputePlan(drag, point);
        TabDragTrace.Log($"RELEASE {Describe(drag.Entry)} at ({point.X:0},{point.Y:0}): {plan.Outcome}, order {Names(plan.Order)}");

        // The strip is changed after the event is over: moving the pressed item inside its own pointer event is what WinUI does not survive.
        AfterDrag(() =>
        {
            ResetDragVisuals();
            FinishTabDrag(drag, plan);
        });
    }

    private void OnTabPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId)
        {
            return;
        }

        _drag = null;
        TabDragTrace.Log($"LOST the pointer during {Describe(drag.Entry)} (active {drag.Active}): drag cancelled");
        if (drag.Active)
        {
            ResetDragVisuals();
            SyncTabs();
        }
    }

    /// <summary>The pointer moved far enough: note where everything stands, and let the other tabs slide.</summary>
    private void BeginTabDrag(DragSession drag)
    {
        drag.Active = true;
        foreach (var item in TabsView.TabItems.OfType<TabViewItem>())
        {
            if (EntryOf(item) is not { } entry)
            {
                continue;
            }

            var shown = item.Visibility == Visibility.Visible && item.ActualWidth > 0;
            var left = item.TransformToVisual(TabsView).TransformPoint(new Point(0, 0)).X;
            drag.Slots.Add(new DragSlot(entry, left, shown ? item.ActualWidth : 0, shown));
            drag.Items[entry] = item;
        }

        var own = drag.Slots.FirstOrDefault(s => s.Entry.Equals(drag.Entry));
        drag.Left = own?.Left ?? 0;
        drag.Width = own?.Width ?? drag.Item.ActualWidth;
        drag.Unit.Add(drag.Item);
        if (drag.Entry is ChipEntry chip)
        {
            foreach (var member in _viewModel.Tabs.TabsOf(chip.Group))
            {
                if (drag.Items.TryGetValue(new TabEntry(member), out var memberItem))
                {
                    drag.Unit.Add(memberItem);
                }
            }
        }

        foreach (var (entry, item) in drag.Items)
        {
            if (!drag.Unit.Contains(item))
            {
                item.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
            }
        }

        foreach (var item in drag.Unit)
        {
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(item, 100);
        }

        TabDragTrace.Log($"BEGIN {Describe(drag.Entry)}: slots {string.Join(" ", drag.Slots.Select(s => $"{Describe(s.Entry)}@{s.Left:0}+{s.Width:0}{(s.Visible ? string.Empty : "(hidden)")}"))}; strip {TabsView.ActualWidth:0}x{TabsView.ActualHeight:0}");
    }

    private DragPlan ComputePlan(DragSession drag, Point point)
    {
        var centre = drag.Left + (point.X - drag.Start.X) + (drag.Width / 2);
        return TabDragPlanner.Plan(_viewModel.Tabs, drag.Slots, drag.Entry, centre, point.X, point.Y, TabsView.ActualWidth, TabsView.ActualHeight);
    }

    /// <summary>Draws the plan: the dragged item follows the pointer, the others slide aside, and the item is tinted when letting go would tear it off.</summary>
    private void ShowPlan(DragSession drag, DragPlan plan, Point point)
    {
        var dx = (float)(point.X - drag.Start.X);
        foreach (var item in drag.Unit)
        {
            item.Translation = new Vector3(dx, 0, 0);
        }

        foreach (var (entry, item) in drag.Items)
        {
            if (drag.Unit.Contains(item))
            {
                continue;
            }

            item.Translation = new Vector3((float)plan.Shifts.GetValueOrDefault(entry), 0, 0);
        }

        SetTearing(drag.Item, plan.Outcome == DragOutcome.TearOff);

        var signature = $"{plan.Outcome}:{Names(plan.Order)}";
        if (signature != drag.LastSignature)
        {
            drag.LastSignature = signature;
            TabDragTrace.Log($"PLAN {plan.Outcome} at ({point.X:0},{point.Y:0}); order {Names(plan.Order)}");
        }
    }

    private void SetTearing(TabViewItem item, bool tearing)
    {
        if (item.Tag is TabViewModel tab && _tabUi.TryGetValue(tab, out var ui))
        {
            ui.SetTearing(tearing);
        }
        else
        {
            item.Opacity = tearing ? 0.6 : 1;
        }
    }

    private void ResetDragVisuals()
    {
        foreach (var item in TabsView.TabItems.OfType<TabViewItem>())
        {
            item.Translation = Vector3.Zero;
            item.TranslationTransition = null;
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(item, 0);
            SetTearing(item, false);
        }
    }

    /// <summary>Carries out what the drag decided.</summary>
    private void FinishTabDrag(DragSession drag, DragPlan plan)
    {
        if (plan.Outcome == DragOutcome.Reorder)
        {
            var change = StripReorder.Decide(_viewModel.Tabs, plan.Order, drag.Entry);
            TabDragTrace.Log($"FINISH reorder: {change.Kind}, index {change.Index}; model order before {ModelOrder()}");
            ApplyStripChange(change);
            TabDragTrace.Log($"FINISH done; model order {ModelOrder()}; strip order {StripOrder()}");
            return;
        }

        if (!NativeCursor.TryGetPosition(out var x, out var y))
        {
            TabDragTrace.Log("FINISH tear off: the cursor position is unknown, nothing done");
            SyncTabs();
            return;
        }

        foreach (var other in App.OpenWindows)
        {
            if (!ReferenceEquals(other, this) && other.TryAcceptDrop(drag.Entry, x, y))
            {
                TabDragTrace.Log($"FINISH joined another window's strip at {x},{y}");
                return;
            }
        }

        TabDragTrace.Log($"FINISH tear off into a new window at {x},{y}");
        switch (drag.Entry)
        {
            case TabEntry tab:
                Run(CommandIds.TabTearOff, tab.Tab.History.Current.Value, new TabTearOff(tab.Tab, x - 60, y - 24));
                break;
            case ChipEntry chip:
                Run(CommandIds.TabGroupTearOff, null, new TabGroupTearOff(chip.Group, x - 60, y - 24));
                break;
        }
    }

    /// <summary>An item let go over this window's tab row by a drag that started in another window: it joins this strip where the pointer is.</summary>
    internal bool TryAcceptDrop(StripEntry entry, int screenX, int screenY)
    {
        if (NearStripX(screenX, screenY) is not { } x)
        {
            return false;
        }

        var index = IndexAt(x);
        switch (entry)
        {
            case TabEntry tab:
                Run(CommandIds.TabDrop, tab.Tab.History.Current.Value, new TabDrop(tab.Tab, index));
                break;
            case ChipEntry chip:
                Run(CommandIds.TabGroupDrop, null, new TabGroupDrop(chip.Group, index));
                break;
        }

        return true;
    }

    private string ModelOrder() => string.Join(" | ", _viewModel.Tabs.Tabs.Select(t => (t.IsPinned ? "*" : string.Empty) + t.Title));

    private string StripOrder() => string.Join(" | ", TabsView.TabItems.OfType<TabViewItem>().Select(i => EntryOf(i) is { } e ? Describe(e) : "?"));

    private static string Describe(StripEntry entry) => entry switch
    {
        TabEntry tab => tab.Tab.Title,
        ChipEntry chip => "[" + chip.Group.Name + "]",
        _ => "?",
    };

    private static string Names(IEnumerable<StripEntry> order) => string.Join(" | ", order.Select(Describe));
}
