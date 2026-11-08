using Leviathan.Core.Platform;

namespace Leviathan.ViewModels.Playback;

/// <summary>
/// What the playback keys of spec §10.2 mean, worked out on top of an <see cref="IVideoEngine"/> so every engine and every place
/// that plays (preview pane, triage mode, floating preview) behaves the same. The engine only has absolute seeks; the jumps, the
/// percent keys, Home/End, the speed ladder and the J/K/L skim are decided here, which also makes them testable with a fake.
/// </summary>
public sealed class PlaybackController(IVideoEngine engine, CancellationToken cancellation = default)
{
    /// <summary>The speeds [ and ] step through (spec §10.2: 0.5× to 4×).</summary>
    public static readonly IReadOnlyList<double> SpeedLadder = [0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>The forward speeds L steps through (1×, 2×, 4×, 8×); J goes back five seconds times each.</summary>
    public static readonly IReadOnlyList<double> SkimLadder = [1, 2, 4, 8];

    /// <summary>"Last 10 s" for the End key.</summary>
    public static readonly TimeSpan EndLead = TimeSpan.FromSeconds(10);

    private int _skim;

    /// <summary>The player.</summary>
    public IVideoEngine Engine => engine;

    /// <summary>Space: play when paused or ended (from the start when it ended), pause when playing.</summary>
    public async Task TogglePlayAsync()
    {
        _skim = 0;
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
        _skim = 0;
        if (engine.Snapshot is { IsOpen: true } s)
        {
            await engine.SeekAsync(Clamp(s.Position + delta, s.Duration), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>A click on a filmstrip frame or the waveform: to <paramref name="position"/>, kept inside the file.</summary>
    public async Task SeekToAsync(TimeSpan position)
    {
        _skim = 0;
        if (engine.Snapshot is { IsOpen: true } s)
        {
            await engine.SeekAsync(Clamp(position, s.Duration), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Ctrl+1 … Ctrl+9: to <paramref name="tenths"/> tenths of the way through (1 = 10%).</summary>
    public async Task JumpToTenthAsync(int tenths)
    {
        _skim = 0;
        if (engine.Snapshot is { IsOpen: true } s && s.Duration > TimeSpan.Zero)
        {
            await engine.SeekAsync(Clamp(s.Duration * (Math.Clamp(tenths, 0, 9) / 10.0), s.Duration), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Home: back to the start.</summary>
    public Task HomeAsync()
    {
        _skim = 0;
        return engine.Snapshot.IsOpen ? engine.SeekAsync(TimeSpan.Zero, cancellation) : Task.CompletedTask;
    }

    /// <summary>End: to the last ten seconds (or the start of a shorter file).</summary>
    public async Task EndAsync()
    {
        _skim = 0;
        if (engine.Snapshot is { IsOpen: true } s)
        {
            await engine.SeekAsync(Clamp(s.Duration - EndLead, s.Duration), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>. and ,: one frame forward or back; a playing video is paused by it.</summary>
    public Task StepFrameAsync(bool forward)
    {
        _skim = 0;
        return engine.Snapshot.IsOpen ? engine.StepFrameAsync(forward, cancellation) : Task.CompletedTask;
    }

    /// <summary>]: the next speed up the ladder (stays at the top).</summary>
    public Task SpeedUpAsync() => StepSpeedAsync(+1);

    /// <summary>[: the next speed down the ladder (stays at the bottom).</summary>
    public Task SlowDownAsync() => StepSpeedAsync(-1);

    /// <summary>K: pause, and forget the skim level.</summary>
    public async Task PauseAsync()
    {
        _skim = 0;
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
        _skim = 0;
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
