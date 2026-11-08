using System.Runtime.InteropServices;
using Leviathan.Core.Model;
using Leviathan.ViewModels.Tabs;
using Microsoft.UI.Dispatching;
using VirtualKey = Windows.System.VirtualKey;

namespace Leviathan.App;

/// <summary>
/// Slow double-click to rename (spec §8.4): arms a rename when <see cref="SlowClickDetector"/> says a click was the slow second click
/// on the selected item, and starts it once the double-click time has passed with no further press, double-click or drag. Each file view
/// owns one so the details list and the tiles behave alike.
/// </summary>
internal sealed partial class SlowRenameTrigger
{
    private readonly SlowClickDetector _detector = new(TimeProvider.System, TimeSpan.FromMilliseconds(GetDoubleClickTime()));
    private readonly DispatcherQueueTimer _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();

    /// <summary>Creates the trigger on the UI thread.</summary>
    public SlowRenameTrigger() => _timer.IsRepeating = false;

    /// <summary>Records a press; <paramref name="begin"/> runs when the rename should start.</summary>
    public void Press(FileEntry entry, bool wasOnlySelection, bool plain, Action<FileEntry> begin)
    {
        Interrupt();
        if (!_detector.Press(entry.Location, wasOnlySelection, plain))
        {
            return;
        }

        _timer.Interval = _detector.DoubleClick + TimeSpan.FromMilliseconds(80);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _pending = (entry, begin);
        _timer.Start();
    }

    private (FileEntry Entry, Action<FileEntry> Begin)? _pending;

    /// <summary>Drops a rename that is waiting to start, but remembers the last click (a new press is about to be judged).</summary>
    public void Interrupt()
    {
        _timer.Stop();
        _pending = null;
    }

    /// <summary>A double-click or a new folder: nothing should start, and the last click is forgotten.</summary>
    public void Cancel()
    {
        Interrupt();
        _detector.Reset();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();

        // The button still held means a drag or a long press is in progress, not a slow click.
        if (_pending is { } pending && !KeyTranslator.IsDown(VirtualKey.LeftButton))
        {
            _pending = null;
            _detector.Reset();
            pending.Begin(pending.Entry);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();
}
