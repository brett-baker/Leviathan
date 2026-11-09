using Leviathan.Core.Platform;

namespace Leviathan.ViewModels.Playback;

/// <summary>
/// What the playback keys of spec §10.2 mean, worked out on top of an <see cref="IVideoEngine"/> so every engine and every place
/// that plays (preview pane, triage mode, floating preview) behaves the same. The engine only has absolute seeks; the jumps, the
/// percent keys, Home/End, the speed ladder and the J/K/L skim are decided here, which also makes them testable with a fake.
/// </summary>
public sealed class PlaybackController(IVideoEngine engine, TimeProvider? time = null, CancellationToken cancellation = default)
{
    /// <summary>The speeds [ and ] step through (spec §10.2: 0.5× to 4×).</summary>
    public static readonly IReadOnlyList<double> SpeedLadder = [0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>The forward speeds L steps through (1×, 2×, 4×, 8×); J goes back five seconds times each.</summary>
    public static readonly IReadOnlyList<double> SkimLadder = [1, 2, 4, 8];

    /// <summary>"Last 10 s" for the End key.</summary>
    public static readonly TimeSpan EndLead = TimeSpan.FromSeconds(10);

    private int _skim;
    private CancellationTokenSource? _burst;

    /// <summary>The player.</summary>
    public IVideoEngine Engine => engine;

    /// <summary>True while S skim is stepping through the file in bursts.</summary>
    public bool IsBurstSkimming => _burst is not null;

    /// <summary>Raised (on any thread) when burst skimming starts or stops.</summary>
    public event EventHandler? BurstSkimChanged;

    /// <summary>
    /// S: starts or stops skim playback (spec §10.3). It plays <paramref name="burst"/> at evenly spaced points, one every
    /// <paramref name="interval"/> of the duration (0.05 = every 5%), from the next point after the current position, then pauses at the
    /// end. Any other playback key stops it. The next point is sought as soon as a burst ends, so there is one seek between bursts rather
    /// than a decoded-ahead join (the engines have a single stream each).
    /// </summary>
    public Task ToggleBurstSkimAsync(TimeSpan burst, double interval)
    {
        if (_burst is not null)
        {
            StopBurst();
            return Task.CompletedTask;
        }

        var s = engine.Snapshot;
        if (!s.IsOpen || s.Duration <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        _skim = 0;
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _burst = source;
        BurstSkimChanged?.Invoke(this, EventArgs.Empty);

        // The key returns at once; the bursts run on until the end, a stop or a cancel.
        BurstTask = RunBurstAsync(s, source, burst, interval);
        return Task.CompletedTask;
    }

    /// <summary>The running skim, for tests to wait on; completed or null when none runs.</summary>
    public Task? BurstTask { get; private set; }

    private async Task RunBurstAsync(PlaybackSnapshot s, CancellationTokenSource source, TimeSpan burst, double interval)
    {
        try
        {
            await engine.SetSpeedAsync(1, source.Token).ConfigureAwait(false);
            foreach (var point in SkimPoints(s.Duration, s.Position, burst, interval))
            {
                await engine.SeekAsync(point, source.Token).ConfigureAwait(false);
                if (engine.Snapshot.State != PlaybackState.Playing)
                {
                    await engine.PlayAsync(source.Token).ConfigureAwait(false);
                }

                await Task.Delay(burst, time ?? TimeProvider.System, source.Token).ConfigureAwait(false);
                if (!engine.Snapshot.IsOpen)
                {
                    break;
                }
            }

            if (engine.Snapshot.State == PlaybackState.Playing)
            {
                await engine.PauseAsync(source.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by a key, or the file changed.
        }
        finally
        {
            if (ReferenceEquals(_burst, source))
            {
                _burst = null;
                BurstSkimChanged?.Invoke(this, EventArgs.Empty);
            }

            source.Dispose();
        }
    }

    /// <summary>The start of each burst: every <paramref name="interval"/> of <paramref name="duration"/>, from the first one at or after <paramref name="from"/>, each kept far enough from the end to play a whole burst.</summary>
    public static IReadOnlyList<TimeSpan> SkimPoints(TimeSpan duration, TimeSpan from, TimeSpan burst, double interval)
    {
        interval = Math.Clamp(interval, 0.01, 0.5);
        var last = duration > burst ? duration - burst : TimeSpan.Zero;
        var points = new List<TimeSpan>();
        for (var i = 0; i * interval < 1 - 1e-9; i++)
        {
            var point = duration * (i * interval);
            if (point > last)
            {
                point = last;
            }

            if (point >= from - TimeSpan.FromMilliseconds(1) && (points.Count == 0 || point > points[^1]))
            {
                points.Add(point);
            }
        }

        return points;
    }

    /// <summary>Stops skim playback, if it is running (a new file was opened).</summary>
    public void StopBurstSkim() => StopBurst();

    private void StopBurst()
    {
        var source = _burst;
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    private void Reset()
    {
        _skim = 0;
        StopBurst();
    }

    /// <summary>Space: play when paused or ended (from the start when it ended), pause when playing.</summary>
    public async Task TogglePlayAsync()
    {
        Reset();
        switch (engine.Snapshot.State)
        {
            case PlaybackState.Playing:
                await engine.PauseAsync(cancellation).ConfigureAwait(false);
                break;
            case PlaybackState.Ended:
                await engine.SeekAsync(TimeSpan.Zero, cancellation).ConfigureAwait(false);
                await engine.PlayAsync(cancellation).ConfigureAwait(false);
                break;
            case PlaybackState.Paused:
                await engine.PlayAsync(cancellation).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Left/Right and their Shift, Ctrl and Alt forms: a jump of <paramref name="delta"/> (negative = back), kept inside the file.</summary>
    public async Task JumpAsync(TimeSpan delta)
    {
        Reset();
        if (engine.Snapshot is { IsOpen: true } s)
        {
            await engine.SeekAsync(Clamp(s.Position + delta, s.Duration), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>A click on a filmstrip frame or the waveform: to <paramref name="position"/>, kept inside the file.</summary>
    public async Task SeekToAsync(TimeSpan position)
    {
        Reset();
        if (engine.Snapshot is { IsOpen: true } s)
        {
            await engine.SeekAsync(Clamp(position, s.Duration), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Ctrl+1 … Ctrl+9: to <paramref name="tenths"/> tenths of the way through (1 = 10%).</summary>
    public async Task JumpToTenthAsync(int tenths)
    {
        Reset();
        if (engine.Snapshot is { IsOpen: true } s && s.Duration > TimeSpan.Zero)
        {
            await engine.SeekAsync(Clamp(s.Duration * (Math.Clamp(tenths, 0, 9) / 10.0), s.Duration), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Home: back to the start.</summary>
    public Task HomeAsync()
    {
        Reset();
        return engine.Snapshot.IsOpen ? engine.SeekAsync(TimeSpan.Zero, cancellation) : Task.CompletedTask;
    }

    /// <summary>End: to the last ten seconds (or the start of a shorter file).</summary>
    public async Task EndAsync()
    {
        Reset();
        if (engine.Snapshot is { IsOpen: true } s)
        {
            await engine.SeekAsync(Clamp(s.Duration - EndLead, s.Duration), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>. and ,: one frame forward or back; a playing video is paused by it.</summary>
    public Task StepFrameAsync(bool forward)
    {
        Reset();
        return engine.Snapshot.IsOpen ? engine.StepFrameAsync(forward, cancellation) : Task.CompletedTask;
    }

    /// <summary>]: the next speed up the ladder (stays at the top).</summary>
    public Task SpeedUpAsync() => StepSpeedAsync(+1);

    /// <summary>[: the next speed down the ladder (stays at the bottom).</summary>
    public Task SlowDownAsync() => StepSpeedAsync(-1);

    /// <summary>K: pause, and forget the skim level.</summary>
    public async Task PauseAsync()
    {
        Reset();
        if (engine.Snapshot.State == PlaybackState.Playing)
        {
            await engine.PauseAsync(cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>L: play forward at 1×, and each press after that doubles the speed up to 8×.</summary>
    public async Task SkimForwardAsync()
    {
        var s = engine.Snapshot;
        if (!s.IsOpen)
        {
            return;
        }

        // After backward skimming, L starts again from 1×.
        StopBurst();
        _skim = _skim <= 0 ? 1 : Math.Min(_skim + 1, SkimLadder.Count);
        await engine.SetSpeedAsync(SkimLadder[_skim - 1], cancellation).ConfigureAwait(false);
        if (s.State != PlaybackState.Playing)
        {
            await engine.PlayAsync(cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// J: skim backward. An engine cannot play a file in reverse smoothly, so each press pauses and goes back one stride: 5 s, then 10,
    /// 20 and 40 s as the presses repeat (L's ladder mirrored), which on a long file finds a place far faster than dragging.
    /// </summary>
    public async Task SkimBackAsync()
    {
        var s = engine.Snapshot;
        if (!s.IsOpen)
        {
            return;
        }

        StopBurst();
        _skim = _skim >= 0 ? -1 : Math.Max(_skim - 1, -SkimLadder.Count);
        await engine.SetSpeedAsync(1, cancellation).ConfigureAwait(false);
        if (s.State == PlaybackState.Playing)
        {
            await engine.PauseAsync(cancellation).ConfigureAwait(false);
        }

        await engine.SeekAsync(Clamp(s.Position - TimeSpan.FromSeconds(5 * SkimLadder[-_skim - 1]), s.Duration), cancellation).ConfigureAwait(false);
    }

    private async Task StepSpeedAsync(int direction)
    {
        Reset();
        var speed = engine.Snapshot.Speed;
        var index = 0;
        for (var i = 0; i < SpeedLadder.Count; i++)
        {
            // The nearest rung, so a speed set elsewhere (such as L's 8×) joins the ladder sensibly.
            if (Math.Abs(SpeedLadder[i] - speed) < Math.Abs(SpeedLadder[index] - speed))
            {
                index = i;
            }
        }

        await engine.SetSpeedAsync(SpeedLadder[Math.Clamp(index + direction, 0, SpeedLadder.Count - 1)], cancellation).ConfigureAwait(false);
    }

    private static TimeSpan Clamp(TimeSpan position, TimeSpan duration) =>
        position < TimeSpan.Zero ? TimeSpan.Zero : duration > TimeSpan.Zero && position > duration ? duration : position;
}
