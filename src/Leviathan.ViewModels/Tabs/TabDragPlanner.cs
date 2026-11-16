namespace Leviathan.ViewModels.Tabs;

/// <summary>One visible-or-hidden item of the tab strip with where it stands, in the strip's own device-independent pixels.</summary>
/// <param name="Entry">The tab or chip.</param>
/// <param name="Left">Left edge.</param>
/// <param name="Width">Width (0 for an item that is not shown, such as a tab in a folded group).</param>
/// <param name="Visible">Whether the item is shown; hidden items keep their place in the order but never take or give room.</param>
public sealed record DragSlot(StripEntry Entry, double Left, double Width, bool Visible = true);

/// <summary>What letting go would do.</summary>
public enum DragOutcome
{
    /// <summary>The item stays in this strip; <see cref="DragPlan.Order"/> is the strip's order after the drop.</summary>
    Reorder,

    /// <summary>The pointer is clearly away from the strip: the item would leave as its own window (or join another window's strip).</summary>
    TearOff,
}

/// <summary>The answer to "where would the dragged item go if the user let go now?".</summary>
/// <param name="Outcome">Stay in the strip, or leave it.</param>
/// <param name="Order">The strip's items in their order after the drop; feed it to <see cref="StripReorder.Decide"/> to get the model change.</param>
/// <param name="Shifts">For each other shown item that must slide aside to make room for the dragged one, how far (negative = left).</param>
public sealed record DragPlan(DragOutcome Outcome, IReadOnlyList<StripEntry> Order, IReadOnlyDictionary<StripEntry, double> Shifts);

/// <summary>
/// The one place that decides where a dragged tab or chip lands (spec §4.1). It looks at geometry only: where each item stands, where the dragged
/// item's middle is and where the pointer is. No window, no events, so the rules, and each edge case, are plain unit tests. The window feeds it
/// as the pointer moves (to slide the other tabs out of the way and to tint a tab that would tear off) and once more when the button is released.
/// </summary>
public static class TabDragPlanner
{
    /// <summary>How far, in pixels, below the tab row (or above it, half that) the pointer may drift and still be reordering.</summary>
    public const double DefaultSlack = 90;

    /// <summary>A pinned tab stays where it is until it is unpinned (spec §4.1): it cannot be picked up. Everything else can.</summary>
    public static bool CanDrag(StripEntry entry) => entry is not TabEntry { Tab.IsPinned: true };

    /// <summary>
    /// Plans the drop of <paramref name="dragged"/>.
    /// </summary>
    /// <param name="strip">The model; a chip is dragged together with the tabs of its group.</param>
    /// <param name="slots">Every item of the strip, in strip order, with its position before the drag moved anything.</param>
    /// <param name="dragged">The tab or chip being dragged.</param>
    /// <param name="draggedCentre">Where the middle of the dragged item is now (follows the pointer, offset by where it was grabbed).</param>
    /// <param name="pointerX">Pointer x in the strip's pixels.</param>
    /// <param name="pointerY">Pointer y in the strip's pixels (0 is the top of the strip).</param>
    /// <param name="stripWidth">Width of the strip (the window).</param>
    /// <param name="stripHeight">Height of the tab row.</param>
    /// <param name="slack">See <see cref="DefaultSlack"/>.</param>
    public static DragPlan Plan(
        TabStripViewModel strip,
        IReadOnlyList<DragSlot> slots,
        StripEntry dragged,
        double draggedCentre,
        double pointerX,
        double pointerY,
        double stripWidth,
        double stripHeight,
        double slack = DefaultSlack)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(dragged);
        var original = slots.Select(s => s.Entry).ToList();
        var none = new Dictionary<StripEntry, double>();
        if (!CanDrag(dragged) || !original.Contains(dragged))
        {
            return new DragPlan(DragOutcome.Reorder, original, none);
        }

        var away = pointerX < 0 || pointerX > stripWidth || pointerY < -slack / 2 || pointerY > stripHeight + slack;
        if (away)
        {
            return new DragPlan(DragOutcome.TearOff, original, none);
        }

        // A chip carries its group: the tabs of the group are neither targets nor shifted, they travel with it.
        var unit = new HashSet<StripEntry> { dragged };
        if (dragged is ChipEntry chip)
        {
            foreach (var member in strip.TabsOf(chip.Group))
            {
                unit.Add(new TabEntry(member));
            }
        }

        var others = slots.Where(s => s.Visible && !unit.Contains(s.Entry)).ToList();

        // The dragged item goes before the first other item whose middle is right of its own middle; none means the end.
        var anchorIndex = others.FindIndex(s => s.Left + (s.Width / 2) > draggedCentre);
        var anchor = anchorIndex < 0 ? null : others[anchorIndex].Entry;

        var order = original.Where(e => !e.Equals(dragged)).ToList();
        var at = anchor is null ? order.Count : order.IndexOf(anchor);
        order.Insert(at, dragged);

        // Room: the items between the old and the new place slide by the width of what is being dragged.
        var width = slots.Where(s => s.Visible && unit.Contains(s.Entry)).Sum(s => s.Width);
        var draggedAt = original.IndexOf(dragged);
        var before = others.Count(s => original.IndexOf(s.Entry) < draggedAt);
        var shifts = new Dictionary<StripEntry, double>();
        var after = anchorIndex < 0 ? others.Count : anchorIndex;
        if (after > before)
        {
            for (var i = before; i < after; i++)
            {
                shifts[others[i].Entry] = -width;
            }
        }
        else if (after < before)
        {
            for (var i = after; i < before; i++)
            {
                shifts[others[i].Entry] = width;
            }
        }

        return new DragPlan(DragOutcome.Reorder, order, shifts);
    }
}
