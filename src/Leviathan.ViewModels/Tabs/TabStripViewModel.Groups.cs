using System.Collections.ObjectModel;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Tabs;

/// <summary>A group and its tabs, taken out of one strip to be put into another (tear-off and merge of a whole group, spec §4.1).</summary>
/// <param name="Info">The group as it was.</param>
/// <param name="Tabs">Its tabs, in strip order, still naming the group.</param>
public sealed record GroupTransfer(TabGroupInfo Info, IReadOnlyList<TabViewModel> Tabs);

/// <summary>
/// Tab groups of one window (spec §4.1, D32). The rules: the tabs of a group sit side by side; a group lives while it has tabs;
/// pinned tabs are never in a group; Ctrl+Tab and Ctrl+1–9 skip the tabs of a collapsed group; closing a group can be undone as a
/// group. Every operation here leaves those true, so the strip never has to repair them.
/// </summary>
public sealed partial class TabStripViewModel
{
    /// <summary>The groups, in the order they were made. The strip draws each at the position of its first tab.</summary>
    public ObservableCollection<TabGroup> Groups { get; } = [];

    /// <summary>The tabs that can be shown: all but those folded into a collapsed group.</summary>
    public IReadOnlyList<TabViewModel> VisibleTabs => [.. Tabs.Where(t => GroupOf(t) is not { IsCollapsed: true })];

    /// <summary>The group <paramref name="tab"/> is in, or null.</summary>
    public TabGroup? GroupOf(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.GroupId is { } id ? FindGroup(id) : null;
    }

    /// <summary>The tabs of <paramref name="group"/> in strip order.</summary>
    public IReadOnlyList<TabViewModel> TabsOf(TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return [.. Tabs.Where(t => t.GroupId == group.Id)];
    }

    /// <summary>The groups for saving, in strip order.</summary>
    public IReadOnlyList<TabGroupInfo> GroupInfos() =>
        [.. Groups.OrderBy(g => Tabs.ToList().FindIndex(t => t.GroupId == g.Id)).Select(g => g.ToInfo())];

    /// <summary>
    /// Puts <paramref name="tabs"/> into a new group, gathered where the first of them is. Pinned tabs and tabs not in this strip
    /// are left out. A tab leaving another group moves out of it. Returns null when no tab could be grouped.
    /// </summary>
    /// <param name="tabs">The tabs to group.</param>
    /// <param name="name">The label, empty for none.</param>
    /// <param name="color">A colour as <c>#RRGGBB</c>; by default one no other group here has.</param>
    public TabGroup? CreateGroup(IEnumerable<TabViewModel> tabs, string? name = null, string? color = null)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var members = tabs.Distinct().Where(t => Tabs.Contains(t) && !t.IsPinned).OrderBy(Tabs.IndexOf).ToList();
        if (members.Count == 0)
        {
            return null;
        }

        foreach (var tab in members.Where(t => t.GroupId is not null))
        {
            LeaveGroup(tab);
        }

        var group = new TabGroup(Guid.NewGuid(), name?.Trim() ?? string.Empty, TabGroupColors.IsColor(color) ? color! : TabGroupColors.Next(Groups.Select(g => g.Color)));
        Groups.Add(group);
        var anchor = Tabs.IndexOf(members[0]);
        for (var i = 1; i < members.Count; i++)
        {
            Tabs.Move(Tabs.IndexOf(members[i]), anchor + i);
        }

        foreach (var tab in members)
        {
            tab.GroupId = group.Id;
        }

        return group;
    }

    /// <summary>
    /// Adds <paramref name="tab"/> to <paramref name="group"/>, at <paramref name="position"/> among its tabs (the end by default).
    /// A tab already in the group is moved within it. False for a pinned tab or one that is not in this strip.
    /// </summary>
    public bool AddToGroup(TabViewModel tab, TabGroup group, int? position = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(group);
        if (!Tabs.Contains(tab) || tab.IsPinned || FindGroup(group.Id) is null)
        {
            return false;
        }

        if (tab.GroupId is not null && tab.GroupId != group.Id)
        {
            LeaveGroup(tab);
        }

        var index = Tabs.IndexOf(tab);
        var others = Tabs.Where(t => t != tab && t.GroupId == group.Id).ToList();
        var without = Tabs.Where(t => t != tab).ToList();
        var start = others.Count == 0 ? Math.Min(index, without.Count) : without.IndexOf(others[0]);
        var target = start + Math.Clamp(position ?? others.Count, 0, others.Count);
        tab.GroupId = group.Id;
        if (target != index)
        {
            Tabs.Move(index, target);
        }

        return true;
    }

    /// <summary>Takes <paramref name="tab"/> out of its group; it lands right after the group's tabs, as in Edge. False when it had none.</summary>
    public bool RemoveFromGroup(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.GroupId is null || !Tabs.Contains(tab))
        {
            return false;
        }

        LeaveGroup(tab);
        return true;
    }

    /// <summary>Dissolves <paramref name="group"/>: its tabs stay where they are, ungrouped.</summary>
    public bool Ungroup(TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (FindGroup(group.Id) is null)
        {
            return false;
        }

        foreach (var tab in Tabs.Where(t => t.GroupId == group.Id).ToList())
        {
            tab.GroupId = null;
        }

        Groups.Remove(group);
        return true;
    }

    /// <summary>Closes every tab of <paramref name="group"/>. Reopen (Ctrl+Shift+T) brings the whole group back. Returns how many tabs closed.</summary>
    public int CloseGroup(TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var batch = Guid.NewGuid();
        var count = 0;
        foreach (var tab in TabsOf(group))
        {
            if (Close(tab, force: false, batch))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Folds the group down to its chip or opens it again. A group cannot be folded when it holds every tab there is: there would
    /// be nothing left to show. Folding a group that has the selected tab moves the selection to a neighbouring tab.
    /// </summary>
    public bool SetCollapsed(TabGroup group, bool collapsed)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (FindGroup(group.Id) is null)
        {
            return false;
        }

        if (!collapsed)
        {
            group.IsCollapsed = false;
            return true;
        }

        if (group.IsCollapsed)
        {
            return true;
        }

        var outside = Tabs.Where(t => t.GroupId != group.Id && GroupOf(t) is not { IsCollapsed: true }).ToList();
        if (outside.Count == 0)
        {
            return false;
        }

        if (Selected is { } selected && selected.GroupId == group.Id)
        {
            var range = GroupRange(group.Id)!.Value;
            Select(outside.FirstOrDefault(t => Tabs.IndexOf(t) > range.End) ?? outside[^1]);
        }

        group.IsCollapsed = true;
        return true;
    }

    /// <summary>Opens a tab at the end of <paramref name="group"/> (the group menu's "New tab in group").</summary>
    public TabViewModel? NewTabInGroup(TabGroup group, LocationPath? location = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (GroupRange(group.Id) is not { } range)
        {
            return null;
        }

        var tab = new TabViewModel(_services, location ?? _services.Home()) { GroupId = group.Id };
        Insert(tab, range.End + 1);
        Select(tab);
        return tab;
    }

    /// <summary>
    /// Moves the whole group so its first tab is at <paramref name="index"/> (counted without the group's own tabs, never among
    /// pinned tabs and never into another group, which it hops over).
    /// </summary>
    public bool MoveGroup(TabGroup group, int index)
    {
        ArgumentNullException.ThrowIfNull(group);
        var members = TabsOf(group);
        if (members.Count == 0)
        {
            return false;
        }

        var without = Tabs.Where(t => t.GroupId != group.Id).ToList();
        var pinned = without.Count(t => t.IsPinned);
        var target = Math.Clamp(index, pinned, without.Count);

        // Landing inside another group's block means the nearer edge of that block.
        if (target < without.Count && target > 0 && without[target].GroupId is { } other && without[target - 1].GroupId == other)
        {
            var first = without.FindIndex(t => t.GroupId == other);
            var last = without.FindLastIndex(t => t.GroupId == other);
            target = target - first <= last + 1 - target ? first : last + 1;
        }

        var start = Tabs.IndexOf(members[0]);
        if (target == start)
        {
            return false;
        }

        if (target < start)
        {
            for (var i = 0; i < members.Count; i++)
            {
                Tabs.Move(Tabs.IndexOf(members[i]), target + i);
            }
        }
        else
        {
            for (var i = members.Count - 1; i >= 0; i--)
            {
                Tabs.Move(Tabs.IndexOf(members[i]), target + i);
            }
        }

        return true;
    }

    /// <summary>Takes a whole group out of this strip, for a new window or another window's strip (spec §4.1). Null when the group is not here.</summary>
    public GroupTransfer? DetachGroup(TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var members = TabsOf(group);
        if (members.Count == 0)
        {
            return null;
        }

        var info = group.ToInfo();
        foreach (var tab in members)
        {
            Remove(tab, Tabs.IndexOf(tab));
        }

        return new GroupTransfer(info, members);
    }

    /// <summary>Puts a group taken from another strip into this one at <paramref name="index"/> (the end by default) and shows its first tab.</summary>
    public void AttachGroup(GroupTransfer transfer, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (FindGroup(transfer.Info.Id) is null)
        {
            Groups.Add(TabGroup.FromInfo(transfer.Info));
        }

        var at = index ?? Tabs.Count;
        foreach (var tab in transfer.Tabs)
        {
            tab.IsPinned = false;
            tab.GroupId = transfer.Info.Id;
            Insert(tab, at);
            at = Tabs.IndexOf(tab) + 1;
        }

        if (transfer.Tabs.Count > 0)
        {
            Select(transfer.Tabs[0]);
        }
    }

    private TabGroup? FindGroup(Guid id) => Groups.FirstOrDefault(g => g.Id == id);

    private TabGroupInfo? GroupInfoOf(TabViewModel tab) => GroupOf(tab)?.ToInfo();

    /// <summary>First and last index of a group's tabs, or null for no group or no tabs.</summary>
    private (int Start, int End)? GroupRange(Guid? id)
    {
        if (id is null)
        {
            return null;
        }

        var start = -1;
        var end = -1;
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].GroupId == id)
            {
                start = start < 0 ? i : start;
                end = i;
            }
        }

        return start < 0 ? null : (start, end);
    }

    /// <summary>The range of the group whose tab sits at <paramref name="index"/>, or null when that tab has no group.</summary>
    private (int Start, int End)? GroupRangeAt(int index) =>
        index >= 0 && index < Tabs.Count ? GroupRange(Tabs[index].GroupId) : null;

    /// <summary>Takes the tab out of its group and moves it right after that group's block, so the block stays whole.</summary>
    private void LeaveGroup(TabViewModel tab)
    {
        var id = tab.GroupId;
        tab.GroupId = null;
        if (GroupRange(id) is { } rest)
        {
            var index = Tabs.IndexOf(tab);
            if (index < rest.Start || index > rest.End)
            {
                // Already outside what is left of the block.
                return;
            }

            // Inside or at an edge of the block: after the block. Moving right, the final index is the old end.
            var target = rest.End;
            if (index != target)
            {
                Tabs.Move(index, target);
            }
        }

        PruneIfEmpty(id);
    }

    private void PruneIfEmpty(Guid? id)
    {
        if (id is { } gid && Tabs.All(t => t.GroupId != gid) && FindGroup(gid) is { } group)
        {
            Groups.Remove(group);
        }
    }

    private void ReattachToGroup(TabViewModel tab, TabGroupInfo? info)
    {
        if (info is null)
        {
            tab.GroupId = null;
            return;
        }

        if (FindGroup(info.Id) is null)
        {
            Groups.Add(TabGroup.FromInfo(info));
        }

        tab.GroupId = info.Id;
    }

    /// <summary>
    /// Makes a freshly restored strip obey the rules (a saved file may not): unpinned tabs only in groups, groups that exist,
    /// every group's tabs side by side at the place of its first, and no empty groups.
    /// </summary>
    private void NormalizeGroups()
    {
        foreach (var tab in Tabs)
        {
            if (tab.IsPinned)
            {
                tab.GroupId = null;
            }
            else if (tab.GroupId is { } id && FindGroup(id) is null)
            {
                Groups.Add(new TabGroup(id, string.Empty, TabGroupColors.Next(Groups.Select(g => g.Color))));
            }
        }

        foreach (var group in Groups.Where(g => Tabs.All(t => t.GroupId != g.Id)).ToList())
        {
            Groups.Remove(group);
        }

        var ordered = new List<TabViewModel>(Tabs.Count);
        var emitted = new HashSet<Guid>();
        foreach (var tab in Tabs)
        {
            if (tab.GroupId is not { } id)
            {
                ordered.Add(tab);
            }
            else if (emitted.Add(id))
            {
                ordered.AddRange(Tabs.Where(t => t.GroupId == id));
            }
        }

        if (!ordered.SequenceEqual(Tabs))
        {
            Tabs.Clear();
            foreach (var tab in ordered)
            {
                Tabs.Add(tab);
            }
        }
    }
}
