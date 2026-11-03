namespace Leviathan.ViewModels.Tabs;

/// <summary>One item of the visible tab strip: a group's chip or a tab.</summary>
public abstract record StripEntry;

/// <summary>A group's label chip.</summary>
/// <param name="Group">The group.</param>
public sealed record ChipEntry(TabGroup Group) : StripEntry;

/// <summary>A tab.</summary>
/// <param name="Tab">The tab.</param>
public sealed record TabEntry(TabViewModel Tab) : StripEntry;

/// <summary>What a drag inside the strip means.</summary>
public enum StripChangeKind
{
    /// <summary>Nothing the model needs to hear about; redraw from the model.</summary>
    None,

    /// <summary>The whole group moved: <see cref="StripChange.Group"/> to <see cref="StripChange.Index"/>.</summary>
    MoveGroup,

    /// <summary>A tab was dropped into the middle of a group: <see cref="StripChange.Tab"/> joins <see cref="StripChange.Group"/> at <see cref="StripChange.Index"/>.</summary>
    AddToGroup,

    /// <summary>A tab was dragged out of its group and dropped at <see cref="StripChange.Index"/> among the tabs.</summary>
    LeaveGroupAndMove,

    /// <summary>A tab was moved to <see cref="StripChange.Index"/> among the tabs.</summary>
    MoveTab,
}

/// <summary>The meaning of a drag in the strip.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Tab">The tab that moved, for the tab kinds.</param>
/// <param name="Group">The group, for <see cref="StripChangeKind.MoveGroup"/> and <see cref="StripChangeKind.AddToGroup"/>.</param>
/// <param name="Index">The place, as described by <see cref="StripChangeKind"/>.</param>
public sealed record StripChange(StripChangeKind Kind, TabViewModel? Tab = null, TabGroup? Group = null, int Index = 0);

/// <summary>
/// Works out what the user meant by a drag inside the strip (spec §4.1): the strip reorders its own items, and this compares the
/// result with the model. A chip that is no longer where its group starts moved the group; a tab dropped between two tabs of a
/// group (or between the chip and the first tab) joins that group; a tab dragged away from its group leaves it; anything else is a
/// plain reorder. Pure, so every case is tested without a window.
/// </summary>
public static class StripReorder
{
    /// <summary>
    /// Interprets <paramref name="shown"/>, the strip's items after the drag, against <paramref name="strip"/>. A tab crossing a
    /// chip looks the same as the chip crossing the tab, so the item the user dragged is passed in; without it the logic guesses
    /// (a changed tab order means a tab moved, otherwise a chip).
    /// </summary>
    public static StripChange Decide(TabStripViewModel strip, IReadOnlyList<StripEntry> shown, StripEntry? dragged = null)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(shown);
        var shownTabs = shown.OfType<TabEntry>().Select(e => e.Tab).ToList();
        var model = strip.Tabs.ToList();
        if (shownTabs.Count != model.Count || shown.OfType<ChipEntry>().Count() != strip.Groups.Count)
        {
            return new StripChange(StripChangeKind.None);
        }

        var tabOrderChanged = !shownTabs.SequenceEqual(model);
        var chipCandidates = dragged switch
        {
            ChipEntry chipDrag => shown.OfType<ChipEntry>().Where(c => c.Group == chipDrag.Group),
            TabEntry => [],
            _ => tabOrderChanged ? [] : shown.OfType<ChipEntry>(),
        };

        // A chip that is no longer where its group starts was dragged; the group goes where the chip now is.
        foreach (var chip in chipCandidates)
        {
            var members = strip.TabsOf(chip.Group);
            if (members.Count == 0)
            {
                continue;
            }

            var before = shown.Take(shown.ToList().IndexOf(chip)).OfType<TabEntry>().Count(e => !members.Contains(e.Tab));
            if (before != model.IndexOf(members[0]))
            {
                return new StripChange(StripChangeKind.MoveGroup, Group: chip.Group, Index: before);
            }
        }

        if (dragged is ChipEntry)
        {
            return new StripChange(StripChangeKind.None);
        }

        // The tab that moved: the one dragged, else the one whose removal makes both orders equal.
        var moved = dragged is TabEntry tabDrag
            ? tabDrag.Tab
            : shownTabs.FirstOrDefault(t => shownTabs.Where(x => x != t).SequenceEqual(model.Where(x => x != t)));
        if (moved is null || !shownTabs.Contains(moved))
        {
            return new StripChange(StripChangeKind.None);
        }

        var position = shown.ToList().FindIndex(e => e is TabEntry t && t.Tab == moved);
        var previous = position > 0 ? GroupOf(strip, shown[position - 1]) : null;
        var next = position < shown.Count - 1 ? GroupOf(strip, shown[position + 1]) : null;
        var own = strip.GroupOf(moved);
        var index = shownTabs.IndexOf(moved);

        if (previous is not null && previous == next && previous != own)
        {
            var inside = shown.Take(position).OfType<TabEntry>().Count(e => e.Tab.GroupId == previous.Id);
            return new StripChange(StripChangeKind.AddToGroup, moved, previous, inside);
        }

        if (own is not null && previous != own && next != own)
        {
            return new StripChange(StripChangeKind.LeaveGroupAndMove, moved, Index: index);
        }

        return tabOrderChanged ? new StripChange(StripChangeKind.MoveTab, moved, Index: index) : new StripChange(StripChangeKind.None);
    }

    private static TabGroup? GroupOf(TabStripViewModel strip, StripEntry entry) => entry switch
    {
        ChipEntry chip => chip.Group,
        TabEntry tab => strip.GroupOf(tab.Tab),
        _ => null,
    };
}
