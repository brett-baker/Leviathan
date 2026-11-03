using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Tabs;

/// <summary>A closed tab kept so Ctrl+Shift+T can bring it back (spec §4.1).</summary>
/// <param name="State">The tab as it was, including history.</param>
/// <param name="Index">Where it sat in the strip.</param>
/// <param name="Group">The group it was in, so reopening puts it back there (spec §4.1), or null.</param>
/// <param name="Batch">Set for every tab of a group closed together; reopening brings the whole batch back as a group.</param>
public sealed record ClosedTab(TabState State, int Index, TabGroupInfo? Group = null, Guid? Batch = null);

/// <summary>
/// The tabs of one window (spec §4.1): open, duplicate, close, reopen the last 25 closed, reorder, pin, lock, switch by number
/// or in most-recently-used order, close others, and search the overflow list. Pinned tabs always sit on the left, cannot be
/// closed with Ctrl+W and are not touched by "close others".
/// </summary>
public sealed partial class TabStripViewModel : ObservableObject
{
    /// <summary>How many closed tabs can be reopened.</summary>
    public const int ClosedTabCapacity = 25;

    private readonly TabServices _services;
    private readonly List<ClosedTab> _closed = [];
    private readonly List<TabViewModel> _recent = [];

    [ObservableProperty]
    private TabViewModel? _selected;

    /// <summary>Creates an empty strip. Call <see cref="NewTab"/> to open the first tab.</summary>
    public TabStripViewModel(TabServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>The tabs, pinned first.</summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>Use most-recently-used order for Ctrl+Tab (setting).</summary>
    public bool UseMruOrder { get; set; }

    /// <summary>When the last tab closes: reset it to Home instead of asking the window to close (setting).</summary>
    public bool LastTabResetsToHome { get; set; }

    /// <summary>True when a closed tab can be reopened.</summary>
    public bool CanReopen => _closed.Count > 0;

    /// <summary>Raised when the last tab was closed and the window should close.</summary>
    public event EventHandler? LastTabClosed;

    /// <summary>Opens a tab at <paramref name="location"/> (the Home location by default) next to the selected tab.</summary>
    public TabViewModel NewTab(LocationPath? location = null, bool select = true)
    {
        var tab = new TabViewModel(_services, location ?? _services.Home());
        Insert(tab, AfterSelectedIndex());
        if (select)
        {
            Select(tab);
        }

        return tab;
    }

    /// <summary>Opens a copy of <paramref name="tab"/> (path, history, view, sort, split state, selection) right after it.</summary>
    public TabViewModel Duplicate(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var copy = TabViewModel.Restore(tab.CaptureState() with { IsPinned = false }, _services);
        Insert(copy, Tabs.IndexOf(tab) + 1);
        Select(copy);
        return copy;
    }

    /// <summary>
    /// Rebuilds the strip from saved tabs (startup profiles, spec §17.1). The folders are not listed here: each tab lists its folder
    /// when it is first shown, so a dead share in a background tab never delays startup.
    /// </summary>
    /// <param name="states">The tabs in strip order.</param>
    /// <param name="selected">Index of the tab to show; clamped into range.</param>
    /// <param name="groups">The tab groups the states refer to; groups no tab belongs to are dropped, tabs naming a missing group are ungrouped.</param>
    public void RestoreTabs(IReadOnlyList<TabState> states, int selected, IReadOnlyList<TabGroupInfo>? groups = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        foreach (var info in groups ?? [])
        {
            if (Groups.All(g => g.Id != info.Id))
            {
                Groups.Add(TabGroup.FromInfo(info));
            }
        }

        foreach (var state in states)
        {
            Tabs.Add(TabViewModel.Restore(state, _services));
        }

        NormalizeGroups();
        if (Tabs.Count > 0)
        {
            Select(Tabs[Math.Clamp(selected, 0, Tabs.Count - 1)]);
        }
    }

    /// <summary>Adds an existing tab, e.g. one dropped from another window or torn off (spec §4.1 merge).</summary>
    public void Attach(TabViewModel tab, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        Insert(tab, index ?? Tabs.Count);
        Select(tab);
    }

    /// <summary>Removes a tab without closing it, for tear-off and merge. Not added to the closed stack.</summary>
    public bool Detach(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return false;
        }

        Remove(tab, index);

        // The tab leaves for another window, where it starts ungrouped (a whole group moves with DetachGroup).
        tab.GroupId = null;
        return true;
    }

    /// <summary>Closes a tab and remembers it for reopening. A pinned tab is closed only when <paramref name="force"/> is set (its × button).</summary>
    public bool Close(TabViewModel tab, bool force = false) => Close(tab, force, batch: null);

    private bool Close(TabViewModel tab, bool force, Guid? batch)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0 || (tab.IsPinned && !force))
        {
            return false;
        }

        if (Tabs.Count == 1 && LastTabResetsToHome)
        {
            _closed.Add(new ClosedTab(tab.CaptureState(), index, GroupInfoOf(tab), batch));
            TrimClosed();
            RemoveFromGroup(tab);
            _ = tab.NavigateAsync(_services.Home(), CancellationToken.None);
            return true;
        }

        _closed.Add(new ClosedTab(tab.CaptureState(), index, GroupInfoOf(tab), batch));
        TrimClosed();
        Remove(tab, index);
        tab.Dispose();
        OnPropertyChanged(nameof(CanReopen));
        if (Tabs.Count == 0)
        {
            LastTabClosed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>Closes every unpinned tab except <paramref name="keep"/>.</summary>
    public int CloseOthers(TabViewModel keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        return CloseMany(Tabs.Where(t => t != keep && !t.IsPinned));
    }

    /// <summary>Closes every unpinned tab to the right of <paramref name="tab"/>.</summary>
    public int CloseToRight(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        return index < 0 ? 0 : CloseMany(Tabs.Skip(index + 1).Where(t => !t.IsPinned));
    }

    /// <summary>Reopens the most recently closed tab where it was, with its history (Ctrl+Shift+T).</summary>
    public TabViewModel? Reopen()
    {
        if (_closed.Count == 0)
        {
            return null;
        }

        // A group closed as a whole comes back as a whole, in its old order (spec §4.1).
        var batch = _closed[^1].Batch is { } id ? _closed.Where(c => c.Batch == id).ToList() : [_closed[^1]];
        foreach (var entry in batch)
        {
            _closed.Remove(entry);
        }

        // Tabs closed together were closed in strip order, so each one noted the place the previous one left; they go back one
        // after another from the first place.
        TabViewModel? first = null;
        for (var i = 0; i < batch.Count; i++)
        {
            var tab = TabViewModel.Restore(batch[i].State, _services);
            ReattachToGroup(tab, batch[i].Group);
            Insert(tab, batch[0].Index + i);
            first ??= tab;
        }

        Select(first!);
        OnPropertyChanged(nameof(CanReopen));
        return first;
    }

    /// <summary>Makes <paramref name="tab"/> the selected tab.</summary>
    public void Select(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!Tabs.Contains(tab))
        {
            return;
        }

        // Showing a tab of a collapsed group opens the group (it could not be shown otherwise).
        if (GroupOf(tab) is { IsCollapsed: true } collapsed)
        {
            collapsed.IsCollapsed = false;
        }

        _recent.Remove(tab);
        _recent.Insert(0, tab);
        Selected = tab;
    }

    /// <summary>Ctrl+Tab (<paramref name="forward"/>) or Ctrl+Shift+Tab. Honors <see cref="UseMruOrder"/>.</summary>
    public void Cycle(bool forward)
    {
        // Tabs folded into a collapsed group are skipped (spec §4.1).
        var visible = VisibleTabs;
        if (visible.Count < 2 || Selected is null || !visible.Contains(Selected))
        {
            return;
        }

        if (UseMruOrder)
        {
            var mru = _recent.Where(visible.Contains).ToList();
            var position = mru.IndexOf(Selected);
            Select(mru[forward ? (position + 1) % mru.Count : (position - 1 + mru.Count) % mru.Count]);
            return;
        }

        var index = visible.ToList().IndexOf(Selected);
        Select(visible[forward ? (index + 1) % visible.Count : (index - 1 + visible.Count) % visible.Count]);
    }

    /// <summary>Ctrl+1 to Ctrl+8 go to that tab; Ctrl+9 goes to the last tab.</summary>
    public void SelectByNumber(int number)
    {
        var visible = VisibleTabs;
        if (visible.Count == 0 || number is < 1 or > 9)
        {
            return;
        }

        Select(number == 9 ? visible[^1] : visible[Math.Min(number, visible.Count) - 1]);
    }

    /// <summary>Pins or unpins a tab. Pinned tabs move to the end of the pinned group; unpinned ones to its start.</summary>
    public void SetPinned(TabViewModel tab, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0 || tab.IsPinned == pinned)
        {
            return;
        }

        if (pinned && tab.GroupId is not null)
        {
            // Pinned tabs stay outside groups (spec §4.1).
            RemoveFromGroup(tab);
            index = Tabs.IndexOf(tab);
        }

        tab.IsPinned = pinned;
        var target = Tabs.Count(t => t.IsPinned && t != tab);
        Tabs.Move(index, Math.Min(target, Tabs.Count - 1));
    }

    /// <summary>Moves a tab to <paramref name="newIndex"/>, staying inside its zone: pinned tabs among pinned, others among others.</summary>
    public void Move(TabViewModel tab, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var pinnedCount = Tabs.Count(t => t.IsPinned);
        var (low, high) = tab.IsPinned ? (0, pinnedCount - 1) : (pinnedCount, Tabs.Count - 1);

        // A grouped tab moves within its group; leaving it is RemoveFromGroup. An ungrouped tab hops over a group, not into it.
        if (GroupRange(tab.GroupId) is { } own)
        {
            (low, high) = own;
        }

        var target = Math.Clamp(newIndex, low, high);
        if (tab.GroupId is null && GroupRangeAt(target) is { } inside && target != index)
        {
            target = target > index ? inside.End : inside.Start;
        }

        if (target != index)
        {
            Tabs.Move(index, target);
        }
    }

    /// <summary>Moves a tab one place left or right (Ctrl+Shift+PgUp / PgDn).</summary>
    public void Nudge(TabViewModel tab, int delta) => Move(tab, Tabs.IndexOf(tab) + delta);

    /// <summary>Overflow drop-down search: tabs whose label or location contains every word of <paramref name="query"/>.</summary>
    public IReadOnlyList<TabViewModel> Search(string query)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. Tabs.Where(t => words.All(w =>
            t.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || t.History.Current.Value.Contains(w, StringComparison.OrdinalIgnoreCase)))];
    }

    private int CloseMany(IEnumerable<TabViewModel> tabs)
    {
        var count = 0;
        foreach (var tab in tabs.ToList())
        {
            if (Close(tab))
            {
                count++;
            }
        }

        return count;
    }

    private int AfterSelectedIndex() => Selected is null ? Tabs.Count : Tabs.IndexOf(Selected) + 1;

    private void Insert(TabViewModel tab, int index)
    {
        // Unpinned tabs never land among pinned ones.
        var pinnedCount = Tabs.Count(t => t.IsPinned);
        var clamped = tab.IsPinned ? Math.Clamp(index, 0, pinnedCount) : Math.Clamp(index, pinnedCount, Tabs.Count);

        // A tab naming a group that is not here is ungrouped.
        if (tab.GroupId is { } wanted && FindGroup(wanted) is null)
        {
            tab.GroupId = null;
        }

        if (GroupRange(tab.GroupId) is { } own)
        {
            // A grouped tab goes into its group's block or right at its edges.
            clamped = Math.Clamp(clamped, own.Start, own.End + 1);
        }
        else if (GroupRangeAt(clamped) is { } inside && clamped > inside.Start)
        {
            // Never inside somebody else's block (the first member of a group still starting there is fine): after it.
            clamped = inside.End + 1;
        }

        Tabs.Insert(clamped, tab);
    }

    private void Remove(TabViewModel tab, int index)
    {
        var group = tab.GroupId;
        Tabs.RemoveAt(index);
        _recent.Remove(tab);
        PruneIfEmpty(group);
        if (Selected == tab)
        {
            // The tab last used, else the neighbour that slid into its place; tabs folded into a collapsed group are not candidates.
            var visible = VisibleTabs;
            Selected = _recent.FirstOrDefault(visible.Contains)
                ?? (visible.Count == 0 ? (Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)]) : visible[Math.Min(index, visible.Count - 1)]);
            if (Selected is { } shown && GroupOf(shown) is { IsCollapsed: true } folded)
            {
                folded.IsCollapsed = false;
            }
        }
    }

    private void TrimClosed()
    {
        while (_closed.Count > ClosedTabCapacity)
        {
            _closed.RemoveAt(0);
        }

        OnPropertyChanged(nameof(CanReopen));
    }
}
