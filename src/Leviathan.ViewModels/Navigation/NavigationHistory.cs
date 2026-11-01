using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Navigation;

/// <summary>Where a navigation went and which item to select on arrival.</summary>
/// <param name="Location">The folder now shown.</param>
/// <param name="SelectOnArrival">The folder just left; if it is an item of <see cref="Location"/> it is selected and scrolled into view (spec §5.1).</param>
public sealed record NavigationResult(LocationPath Location, LocationPath? SelectOnArrival);

/// <summary>A tab's history in a form that can be copied or saved (duplicate tab, startup profiles).</summary>
/// <param name="Back">Back entries, oldest first.</param>
/// <param name="Current">The current location.</param>
/// <param name="Forward">Forward entries, nearest first.</param>
public sealed record NavigationSnapshot(IReadOnlyList<LocationPath> Back, LocationPath Current, IReadOnlyList<LocationPath> Forward);

/// <summary>
/// Back, forward and up for one tab (spec §5.1). Navigating somewhere new clears the forward list; arriving at the same
/// folder again does not add an entry. The list is capped so a long session cannot grow without bound.
/// </summary>
public sealed class NavigationHistory
{
    /// <summary>Most back entries kept.</summary>
    public const int Capacity = 100;

    private readonly List<LocationPath> _back = [];
    private readonly List<LocationPath> _forward = [];

    /// <summary>Creates a history standing at <paramref name="start"/>.</summary>
    public NavigationHistory(LocationPath start)
    {
        ArgumentNullException.ThrowIfNull(start);
        Current = start;
    }

    /// <summary>The folder shown now.</summary>
    public LocationPath Current { get; private set; }

    /// <summary>True when Back has somewhere to go.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>True when Forward has somewhere to go.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>True when the current folder has a parent.</summary>
    public bool CanGoUp => Current.Parent is not null;

    /// <summary>Back entries for the Back button's drop-down, nearest first.</summary>
    public IReadOnlyList<LocationPath> BackList => [.. Enumerable.Reverse(_back)];

    /// <summary>Forward entries for the Forward button's drop-down, nearest first.</summary>
    public IReadOnlyList<LocationPath> ForwardList => [.. _forward];

    /// <summary>Every ancestor of the current folder, nearest first, for the Up button's drop-down.</summary>
    public IReadOnlyList<LocationPath> UpList
    {
        get
        {
            var list = new List<LocationPath>();
            for (var parent = Current.Parent; parent is not null; parent = parent.Parent)
            {
                list.Add(parent);
            }

            return list;
        }
    }

    /// <summary>Goes to a new folder. Returns null when it is the folder already shown.</summary>
    public NavigationResult? Navigate(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location == Current)
        {
            return null;
        }

        var left = Current;
        _back.Add(Current);
        if (_back.Count > Capacity)
        {
            _back.RemoveAt(0);
        }

        _forward.Clear();
        Current = location;
        return new NavigationResult(location, left);
    }

    /// <summary>Changes the current location without adding history (a typed file path resolving to its folder, a redirect).</summary>
    public void Replace(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        Current = location;
    }

    /// <summary>Goes back <paramref name="steps"/> entries (1 for the Back button, more from its drop-down).</summary>
    public NavigationResult? GoBack(int steps = 1)
    {
        if (steps < 1 || steps > _back.Count)
        {
            return null;
        }

        var left = Current;
        _forward.Insert(0, Current);
        for (var i = 1; i < steps; i++)
        {
            _forward.Insert(0, _back[^1]);
            _back.RemoveAt(_back.Count - 1);
        }

        Current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        return new NavigationResult(Current, left);
    }

    /// <summary>Goes forward <paramref name="steps"/> entries.</summary>
    public NavigationResult? GoForward(int steps = 1)
    {
        if (steps < 1 || steps > _forward.Count)
        {
            return null;
        }

        var left = Current;
        _back.Add(Current);
        for (var i = 1; i < steps; i++)
        {
            _back.Add(_forward[0]);
            _forward.RemoveAt(0);
        }

        Current = _forward[0];
        _forward.RemoveAt(0);
        return new NavigationResult(Current, left);
    }

    /// <summary>Goes to the parent folder; it is added to history like any navigation, and the folder just left is selected.</summary>
    public NavigationResult? GoUp()
    {
        var parent = Current.Parent;
        return parent is null ? null : Navigate(parent);
    }

    /// <summary>Copies the history (duplicate tab, saving a session).</summary>
    public NavigationSnapshot Snapshot() => new([.. _back], Current, [.. _forward]);

    /// <summary>Rebuilds a history from a snapshot, trimming to <see cref="Capacity"/>.</summary>
    public static NavigationHistory FromSnapshot(NavigationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var history = new NavigationHistory(snapshot.Current);
        history._back.AddRange(snapshot.Back.TakeLast(Capacity));
        history._forward.AddRange(snapshot.Forward.Take(Capacity));
        return history;
    }
}
