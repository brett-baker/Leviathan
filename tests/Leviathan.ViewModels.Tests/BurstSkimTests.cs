using Leviathan.Core.Platform;
using Leviathan.Testing;
using Leviathan.ViewModels.Playback;

namespace Leviathan.ViewModels.Tests;

/// <summary>Skim playback, S (spec §10.3): bursts at evenly spaced points.</summary>
public sealed class BurstSkimTests
{
    private static readonly TimeSpan Burst = TimeSpan.FromSeconds(3);

    private static async Task<(FakeVideoEngine Engine, PlaybackController Keys, ManualTimeProvider Time)> OpenAsync(double startSeconds = 0)
    {
        var engine = new FakeVideoEngine();
        await engine.OpenAsync(@"\\nas\inbox\clip.mkv", TimeSpan.FromSeconds(startSeconds), true, CancellationToken.None);
        var time = new ManualTimeProvider();
        return (engine, new PlaybackController(engine, time), time);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "timed out waiting");
            await Task.Delay(5);
        }
    }

    [Fact]
    public void SkimPoints_AreEveryFivePercent_AndTheLastFitsAWholeBurst()
    {
        var points = PlaybackController.SkimPoints(TimeSpan.FromSeconds(600), TimeSpan.Zero, Burst, 0.05);

        Assert.Equal(20, points.Count);
        Assert.Equal(TimeSpan.Zero, points[0]);
        Assert.Equal(TimeSpan.FromSeconds(30), points[1]);
        Assert.Equal(TimeSpan.FromSeconds(570), points[^1]);
    }

    [Fact]
    public void SkimPoints_StartAtTheFirstPointAtOrAfterThePosition()
    {
        var points = PlaybackController.SkimPoints(TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(100), Burst, 0.05);

        Assert.Equal(TimeSpan.FromSeconds(120), points[0]);
    }

    [Fact]
    public void SkimPoints_OfAFileShorterThanABurst_IsTheStartOnly()
    {
        var points = PlaybackController.SkimPoints(TimeSpan.FromSeconds(2), TimeSpan.Zero, Burst, 0.05);

        Assert.Equal([TimeSpan.Zero], points);
    }

    [Fact]
    public async Task Toggle_PlaysEachBurst_ThenPausesAtTheEnd()
    {
        var (engine, keys, time) = await OpenAsync();

        await keys.ToggleBurstSkimAsync(Burst, 0.25);
        Assert.True(keys.IsBurstSkimming);
        foreach (var seconds in new[] { 0, 150, 300, 450 })
        {
            await WaitAsync(() => engine.Snapshot.Position == TimeSpan.FromSeconds(seconds));
            Assert.Equal(PlaybackState.Playing, engine.Snapshot.State);
            await WaitAsync(() => time.Pending > 0);
            time.Advance(Burst);
        }

        await keys.BurstTask!;
        Assert.False(keys.IsBurstSkimming);
        Assert.Equal(PlaybackState.Paused, engine.Snapshot.State);
    }

    [Fact]
    public async Task Toggle_WhileRunning_Stops()
    {
        var (engine, keys, _) = await OpenAsync();
        await keys.ToggleBurstSkimAsync(Burst, 0.05);
        await WaitAsync(() => engine.Snapshot.State == PlaybackState.Playing);

        await keys.ToggleBurstSkimAsync(Burst, 0.05);
        await keys.BurstTask!;

        Assert.False(keys.IsBurstSkimming);
    }

    [Fact]
    public async Task AnyOtherPlaybackKey_StopsTheSkim()
    {
        var (_, keys, _) = await OpenAsync();
        await keys.ToggleBurstSkimAsync(Burst, 0.05);

        await keys.JumpAsync(TimeSpan.FromSeconds(30));
        await keys.BurstTask!;

        Assert.False(keys.IsBurstSkimming);
    }

    [Fact]
    public async Task Toggle_WithNothingOpen_DoesNothing()
    {
        var keys = new PlaybackController(new FakeVideoEngine());

        await keys.ToggleBurstSkimAsync(Burst, 0.05);

        Assert.False(keys.IsBurstSkimming);
    }
}
