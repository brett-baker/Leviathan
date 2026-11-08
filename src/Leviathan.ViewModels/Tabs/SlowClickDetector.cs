using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Tabs;

/// <summary>
/// Decides when a click should start renaming (spec §8.4, as in Explorer): a plain click on the name of the one item that is
/// already the whole selection, made later than the system double-click time after the click before it. A quick second click is
/// half of a double-click (it opens the item), so it never arms a rename. The caller starts a timer when this says so and drops
/// the timer if another press, a double-click or a drag follows. Pure logic over <see cref="TimeProvider"/> so it can be tested.
/// </summary>
public sealed class SlowClickDetector
{
    private readonly TimeProvider _time;
    private LocationPath? _lastItem;
    private long _lastTicks;

    /// <summary>Creates a detector. <paramref name="doubleClick"/> is the system double-click time.</summary>
    public SlowClickDetector(TimeProvider time, TimeSpan doubleClick)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        DoubleClick = doubleClick;
    }

    /// <summary>The system double-click time; a click slower than this after the previous one can be a slow click.</summary>
    public TimeSpan DoubleClick { get; }

    /// <summary>
    /// Records a press on <paramref name="item"/> and says whether a rename should be armed.
    /// </summary>
    /// <param name="item">The item that was pressed.</param>
    /// <param name="wasOnlySelection">True when the item was the only selected item before this press.</param>
    /// <param name="plain">True for a left click with no Ctrl or Shift, on the name.</param>
    public bool Press(LocationPath item, bool wasOnlySelection, bool plain)
    {
        var now = _time.GetTimestamp();
        var sameItem = _lastItem is { } last && last == item;
        var gap = _time.GetElapsedTime(_lastTicks, now);
        _lastItem = item;
        _lastTicks = now;
        return plain && wasOnlySelection && sameItem && gap > DoubleClick;
    }

    /// <summary>Forgets the last click (the selection or folder changed, or the rename started).</summary>
    public void Reset() => _lastItem = null;
}
