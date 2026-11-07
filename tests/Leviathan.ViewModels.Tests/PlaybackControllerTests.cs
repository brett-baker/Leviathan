using Leviathan.Core.Platform;
using Leviathan.Testing;
using Leviathan.ViewModels.Playback;

namespace Leviathan.ViewModels.Tests;

public sealed class PlaybackControllerTests
{
    private static async Task<(FakeVideoEngine Engine, PlaybackController Keys)> OpenAsync(bool autoplay = true, double startSeconds = 0)
    {
        var engine = new FakeVideoEngine();
        await engine.OpenAsync(@"\\nas\inbox\clip.mkv", TimeSpan.FromSeconds(startSeconds), autoplay, CancellationToken.None);
        return (engine, new PlaybackController(engine));
    }

    [Fact]
    public async Task Fake_OpenFailure_EndsInFailed_NotAnException()
    {
        var engine = new FakeVideoEngine();
        engine.Refuse["bad.mp4"] = "Unsupported codec";

        await engine.OpenAsync("bad.mp4", TimeSpan.Zero, true, CancellationToken.None);

        Assert.Equal((PlaybackState.Failed, "Unsupported codec"), (engine.Snapshot.State, engine.Snapshot.Error));
        Assert.False(engine.Snapshot.IsOpen);
    }

    [Fact]
    public async Task Fake_PlayingToTheEnd_Ends_AndRaisesChanged()
    {
        var (engine, _) = await OpenAsync(startSeconds: 595);
        var seen = new List<PlaybackState>();
        engine.Changed += (_, s) => seen.Add(s.State);

        engine.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(PlaybackState.Ended, engine.Snapshot.State);
        Assert.Equal(engine.FileDuration, engine.Snapshot.Position);
        Assert.Equal([PlaybackState.Ended], seen);
    }

    [Fact]
    public async Task TogglePlay_PausesAPlayingVideo_PlaysAPausedOne_AndRestartsAnEndedOne()
    {
        var (engine, keys) = await OpenAsync();

        await keys.TogglePlayAsync();
        Assert.Equal(PlaybackState.Paused, engine.Snapshot.State);

        await keys.TogglePlayAsync();
        Assert.Equal(PlaybackState.Playing, engine.Snapshot.State);

        engine.Advance(TimeSpan.FromHours(1));
        Assert.Equal(PlaybackState.Ended, engine.Snapshot.State);
        await keys.TogglePlayAsync();
        Assert.Equal((PlaybackState.Playing, TimeSpan.Zero), (engine.Snapshot.State, engine.Snapshot.Position));
    }

    [Theory]
    [InlineData(100, 30, 130)]
    [InlineData(100, -30, 70)]
    [InlineData(10, -30, 0)]
    [InlineData(590, 30, 600)]
    [InlineData(100, 300, 400)]
    [InlineData(100, 5, 105)]
    public async Task Jump_MovesByTheDelta_KeptInsideTheFile(double from, double delta, double expected)
    {
        var (engine, keys) = await OpenAsync(startSeconds: from);

        await keys.JumpAsync(TimeSpan.FromSeconds(delta));

        Assert.Equal(TimeSpan.FromSeconds(expected), engine.Snapshot.Position);
    }

    [Fact]
    public async Task Keys_DoNothing_WhenNothingIsOpen()
    {
        var engine = new FakeVideoEngine();
        var keys = new PlaybackController(engine);

        await keys.JumpAsync(TimeSpan.FromSeconds(30));
        await keys.JumpToTenthAsync(5);
        await keys.HomeAsync();
        await keys.EndAsync();
        await keys.StepFrameAsync(true);
        await keys.SkimForwardAsync();
        await keys.SkimBackAsync();
        await keys.TogglePlayAsync();

        Assert.Empty(engine.Calls);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(5, 300)]
    [InlineData(9, 540)]
    [InlineData(0, 0)]
    public async Task JumpToTenth_GoesToThatShareOfTheDuration(int tenths, double expected)
    {
        var (engine, keys) = await OpenAsync();

        await keys.JumpToTenthAsync(tenths);

        Assert.Equal(TimeSpan.FromSeconds(expected), engine.Snapshot.Position);
    }

    [Fact]
    public async Task HomeAndEnd_GoToTheStart_AndTheLastTenSeconds()
    {
        var (engine, keys) = await OpenAsync(startSeconds: 200);

        await keys.EndAsync();
        Assert.Equal(TimeSpan.FromSeconds(590), engine.Snapshot.Position);

        await keys.HomeAsync();
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);

        engine.FileDuration = TimeSpan.FromSeconds(4);
        await engine.OpenAsync("short.mp4", TimeSpan.FromSeconds(3), true, CancellationToken.None);
        await keys.EndAsync();
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
    }

    [Fact]
    public async Task StepFrame_PausesAPlayingVideo_AndMovesByOneFrame()
    {
        var (engine, keys) = await OpenAsync(startSeconds: 10);

        await keys.StepFrameAsync(forward: true);
        Assert.Equal((PlaybackState.Paused, TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(40)), (engine.Snapshot.State, engine.Snapshot.Position));

        await keys.StepFrameAsync(forward: false);
        Assert.Equal(TimeSpan.FromSeconds(10), engine.Snapshot.Position);
    }

    [Fact]
    public async Task Speed_StepsUpAndDownTheLadder_AndStopsAtTheEnds()
    {
        var (engine, keys) = await OpenAsync();

        await keys.SpeedUpAsync();
        Assert.Equal(1.25, engine.Snapshot.Speed);

        for (var i = 0; i < 10; i++)
        {
            await keys.SpeedUpAsync();
        }

        Assert.Equal(4, engine.Snapshot.Speed);

        for (var i = 0; i < 12; i++)
        {
            await keys.SlowDownAsync();
        }

        Assert.Equal(0.5, engine.Snapshot.Speed);
    }

    [Fact]
    public async Task Speed_FromASpeedBetweenRungs_JoinsTheLadderAtTheNearestOne()
    {
        var (engine, keys) = await OpenAsync();
        await engine.SetSpeedAsync(8, CancellationToken.None);

        await keys.SlowDownAsync();

        Assert.Equal(3, engine.Snapshot.Speed);
    }

    [Fact]
    public async Task L_PlaysForwardAt1x_ThenDoublesUpTo8x()
    {
        var (engine, keys) = await OpenAsync(autoplay: false);

        await keys.SkimForwardAsync();
        Assert.Equal((PlaybackState.Playing, 1.0), (engine.Snapshot.State, engine.Snapshot.Speed));

        var speeds = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            await keys.SkimForwardAsync();
            speeds.Add(engine.Snapshot.Speed);
        }

        Assert.Equal([2.0, 4.0, 8.0, 8.0], speeds);
    }

    [Fact]
    public async Task J_PausesAndGoesBackInGrowingStrides_ThenLStartsAgainAt1x()
    {
        var (engine, keys) = await OpenAsync(startSeconds: 300);

        await keys.SkimBackAsync();
        Assert.Equal((PlaybackState.Paused, TimeSpan.FromSeconds(295)), (engine.Snapshot.State, engine.Snapshot.Position));

        await keys.SkimBackAsync();
        await keys.SkimBackAsync();
        Assert.Equal(TimeSpan.FromSeconds(295 - 10 - 20), engine.Snapshot.Position);

        await keys.SkimForwardAsync();
        Assert.Equal((PlaybackState.Playing, 1.0), (engine.Snapshot.State, engine.Snapshot.Speed));
    }

    [Fact]
    public async Task K_PausesAndResetsTheSkimLevel()
    {
        var (engine, keys) = await OpenAsync();
        await keys.SkimForwardAsync();
        await keys.SkimForwardAsync();

        await keys.PauseAsync();
        await keys.SkimForwardAsync();

        Assert.Equal(1.0, engine.Snapshot.Speed);
    }
}
