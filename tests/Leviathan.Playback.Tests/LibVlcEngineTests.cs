using Leviathan.Core.Platform;
using Leviathan.Media;
using LibVLCSharp.Shared;

namespace Leviathan.Playback.Tests;

/// <summary>A 20 second generated clip and a headless LibVLC (no window, no sound device), made once for the tests below.</summary>
public sealed class VlcFixture : IAsyncLifetime
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "leviathan-vlc-" + Guid.NewGuid().ToString("N"));

    public string Clip => Path.Combine(Folder, "clip ✓ 日本語 (1).mp4");

    public string Broken => Path.Combine(Folder, "broken.mp4");

    public LibVLC? Vlc { get; private set; }

    public string? Missing { get; private set; }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Broken, [1, 2, 3, 4, 5, 6, 7, 8]);
        try
        {
            Vlc = LibVlcEngine.CreateLibVlc("--vout=dummy", "--aout=dummy", "--no-plugins-cache");
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or BadImageFormatException)
        {
            Missing = "LibVLC's native libraries are not available here: " + ex.Message;
            return;
        }

        if (FfmpegLocator.Create().Locate().FfmpegPath is not { } ffmpeg)
        {
            Missing = "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH), so there is no clip to play.";
            return;
        }

        var made = await new SystemProcessRunner().RunAsync(
            ffmpeg,
            ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=20:size=160x90:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=20", "-c:v", "libx264", "-g", "25", "-c:a", "aac", "-shortest", Clip],
            TimeSpan.FromSeconds(60), 1 << 20, CancellationToken.None);
        if (made.ExitCode != 0)
        {
            Missing = "ffmpeg could not make the test clip: " + made.Error;
        }
    }

    public ValueTask DisposeAsync()
    {
        Vlc?.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class LibVlcEngineTests(VlcFixture fixture) : IClassFixture<VlcFixture>
{
    private LibVlcEngine Engine()
    {
        Assert.SkipWhen(fixture.Missing is not null, fixture.Missing ?? string.Empty);
        return new LibVlcEngine(fixture.Vlc!);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "Timed out waiting for " + what);
    }

    [Fact]
    public async Task Open_PlaysTheClip_WithItsLengthAndFrameRate_FromAnOddlyNamedPath()
    {
        await using var engine = Engine();

        await engine.OpenAsync(fixture.Clip, TimeSpan.Zero, autoplay: true, CancellationToken.None);

        Assert.Equal(PlaybackState.Playing, engine.Snapshot.State);
        await WaitUntil(() => engine.Snapshot.Duration > TimeSpan.Zero, "the length");
        Assert.InRange(engine.Snapshot.Duration.TotalSeconds, 19, 21);
        Assert.Equal(25, engine.Snapshot.FramesPerSecond!.Value, 1);
        await WaitUntil(() => engine.Snapshot.Position > TimeSpan.FromMilliseconds(300), "the position to move");
    }

    [Fact]
    public async Task Open_PausedAtAStart_StaysThere()
    {
        await using var engine = Engine();

        await engine.OpenAsync(fixture.Clip, TimeSpan.FromSeconds(5), autoplay: false, CancellationToken.None);

        await WaitUntil(() => engine.Snapshot.State == PlaybackState.Paused, "pause");
        Assert.InRange(engine.Snapshot.Position.TotalSeconds, 4.5, 6);
    }

    [Fact]
    public async Task Seek_GoesToThePosition_AndPlayAndPauseChangeTheState()
    {
        await using var engine = Engine();
        await engine.OpenAsync(fixture.Clip, TimeSpan.Zero, autoplay: true, CancellationToken.None);
        await engine.PauseAsync(CancellationToken.None);
        await WaitUntil(() => engine.Snapshot.State == PlaybackState.Paused, "pause");

        await engine.SeekAsync(TimeSpan.FromSeconds(12), CancellationToken.None);

        await WaitUntil(() => Math.Abs(engine.Snapshot.Position.TotalSeconds - 12) < 0.5, "the seek");
        await engine.PlayAsync(CancellationToken.None);
        await WaitUntil(() => engine.Snapshot.State == PlaybackState.Playing, "play");
    }

    [Fact]
    public async Task Speed_VolumeAndMute_AreReported()
    {
        await using var engine = Engine();
        await engine.OpenAsync(fixture.Clip, TimeSpan.Zero, autoplay: true, CancellationToken.None);

        await engine.SetSpeedAsync(2, CancellationToken.None);
        await engine.SetVolumeAsync(0.25, CancellationToken.None);
        await engine.SetMutedAsync(true, CancellationToken.None);

        var snapshot = engine.Snapshot;
        Assert.Equal((2.0, 0.25, true), (snapshot.Speed, snapshot.Volume, snapshot.Muted));
    }

    [Fact]
    public async Task StepForward_PausesAndMovesTheFrame()
    {
        await using var engine = Engine();
        await engine.OpenAsync(fixture.Clip, TimeSpan.FromSeconds(3), autoplay: false, CancellationToken.None);
        await WaitUntil(() => engine.Snapshot.State == PlaybackState.Paused, "pause");
        var before = engine.Snapshot.Position;

        await engine.StepFrameAsync(forward: true, CancellationToken.None);

        await WaitUntil(() => engine.Snapshot.Position > before, "the frame to move");
        Assert.Equal(PlaybackState.Paused, engine.Snapshot.State);
    }

    [Fact]
    public async Task Open_ABrokenFile_EndsInFailed_NotAnException()
    {
        await using var engine = Engine();

        await engine.OpenAsync(fixture.Broken, TimeSpan.Zero, autoplay: true, CancellationToken.None);

        await WaitUntil(() => engine.Snapshot.State == PlaybackState.Failed, "failure");
        Assert.False(string.IsNullOrEmpty(engine.Snapshot.Error));
    }

    [Fact]
    public async Task Open_ThenClose_GoesBackToIdle_AndAnotherFileOpens()
    {
        await using var engine = Engine();
        await engine.OpenAsync(fixture.Clip, TimeSpan.Zero, autoplay: true, CancellationToken.None);

        await engine.CloseAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Idle, engine.Snapshot.State);

        await engine.OpenAsync(fixture.Clip, TimeSpan.FromSeconds(2), autoplay: true, CancellationToken.None);
        Assert.Equal(PlaybackState.Playing, engine.Snapshot.State);
    }

    [Fact]
    public async Task Playing_ToTheEnd_Ends_AndSpaceStartsItAgain()
    {
        await using var engine = Engine();
        await engine.OpenAsync(fixture.Clip, TimeSpan.FromSeconds(18.5), autoplay: true, CancellationToken.None);

        await WaitUntil(() => engine.Snapshot.State == PlaybackState.Ended, "the end");
        await engine.PlayAsync(CancellationToken.None);

        await WaitUntil(() => engine.Snapshot.State == PlaybackState.Playing, "playing again");
    }
}
