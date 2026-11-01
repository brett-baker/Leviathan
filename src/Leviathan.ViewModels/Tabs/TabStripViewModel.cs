using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Tabs;

/// <summary>A closed tab kept so Ctrl+Shift+T can bring it back (spec §4.1).</summary>
/// <param name="State">The tab as it was, including history.</param>
/// <param name="Index">Where it sat in the strip.</param>
public sealed record ClosedTab(TabState State, int Index);

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
        return true;
    }

    /// <summary>Closes a tab and remembers it for reopening. A pinned tab is closed only when <paramref name="force"/> is set (its × button).</summary>
    public bool Close(TabViewModel tab, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0 || (tab.IsPinned && !force))
        {
            return false;
        }

        if (Tabs.Count == 1 && LastTabResetsToHome)
        {
            _closed.Add(new ClosedTab(tab.CaptureState(), index));
            TrimClosed();
            _ = tab.NavigateAsync(_services.Home(), CancellationToken.None);
            return true;
        }

        _closed.Add(new ClosedTab(tab.CaptureState(), index));
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

        var closed = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        var tab = TabViewModel.Restore(closed.State, _services);
        Insert(tab, closed.Index);
        Select(tab);
        OnPropertyChanged(nameof(CanReopen));
        return tab;
    }

    /// <summary>Makes <paramref name="tab"/> the selected tab.</summary>
    public void Select(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!Tabs.Contains(tab))
        {
            return;
        }

        _recent.Remove(tab);
        _recent.Insert(0, tab);
        Selected = tab;
    }

    /// <summary>Ctrl+Tab (<paramref name="forward"/>) or Ctrl+Shift+Tab. Honors <see cref="UseMruOrder"/>.</summary>
    public void Cycle(bool forward)
    {
        if (Tabs.Count < 2 || Selected is null)
        {
            return;
        }

        if (UseMruOrder)
        {
            var mru = _recent.Where(Tabs.Contains).ToList();
            var position = mru.IndexOf(Selected);
            Select(mru[forward ? (position + 1) % mru.Count : (position - 1 + mru.Count) % mru.Count]);
            return;
        }

        var index = Tabs.IndexOf(Selected);
        Select(Tabs[forward ? (index + 1) % Tabs.Count : (index - 1 + Tabs.Count) % Tabs.Count]);
    }

    /// <summary>Ctrl+1 to Ctrl+8 go to that tab; Ctrl+9 goes to the last tab.</summary>
    public void SelectByNumber(int number)
    {
        if (Tabs.Count == 0 || number is < 1 or > 9)
        {
            return;
        }

        Select(number == 9 ? Tabs[^1] : Tabs[Math.Min(number, Tabs.Count) - 1]);
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
        var target = Math.Clamp(newIndex, low, high);
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
        Tabs.Insert(clamped, tab);
    }

    private void Remove(TabViewModel tab, int index)
    {
        Tabs.RemoveAt(index);
        _recent.Remove(tab);
        if (Selected == tab)
        {
            // The tab last used, else the neighbour that slid into its place.
            Selected = _recent.FirstOrDefault(Tabs.Contains) ?? (Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)]);
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
