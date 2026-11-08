using System.ComponentModel;

namespace Leviathan.Media.Tests;

/// <summary>The audio picture maker over a fake ffmpeg that writes the output file itself (real temp folders, no real ffmpeg).</summary>
public sealed class AudioArtMakerTests : IDisposable
{
    private static readonly FfmpegTools Found = new(@"C:\ffmpeg\ffprobe.exe", @"C:\ffmpeg\ffmpeg.exe", "setting");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-audioart-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeFfmpeg(Func<IReadOnlyList<string>, ProcessOutcome> behave) : IProcessRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<string>? Last { get; private set; }

        public Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            Last = arguments;
            return Task.FromResult(behave(arguments));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static ProcessOutcome Draws(IReadOnlyList<string> arguments)
    {
        File.WriteAllBytes(arguments[^1], [0xFF, 0xD8, 1, 2, 3]);
        return new ProcessOutcome(0, string.Empty, string.Empty, false, false);
    }

    private AudioArtMaker Maker(FakeFfmpeg runner, FfmpegTools? tools = null) => new(runner, () => tools ?? Found, new ThumbnailDiskCache(_folder));

    private static ThumbnailKey Key(AudioArtKind kind, int width = 600, int height = 120) => AudioArtMaker.KeyFor(kind, @"D:\m\song.flac", 1000, 5, width, height);

    [Fact]
    public void Arguments_ForAWaveform_DrawTheWholeFileAtTheSize()
    {
        var args = AudioArtMaker.Arguments(AudioArtKind.Waveform, @"D:\m\song.flac", "out.jpg", 600, 120, isNetwork: false);

        Assert.Contains("file:D:\\m\\song.flac", args);
        Assert.Contains(args, a => a.Contains("showwavespic=s=600x120", StringComparison.Ordinal));
        Assert.Contains("-vn", args);
        Assert.Equal("out.jpg", args[^1]);
    }

    [Fact]
    public void Arguments_ForCoverArt_TakeTheFirstPictureStreamAndFitTheEdge()
    {
        var args = AudioArtMaker.Arguments(AudioArtKind.Cover, @"D:\m\song.flac", "out.jpg", 300, 0, isNetwork: false);

        Assert.Equal("0:v:0", args[args.ToList().IndexOf("-map") + 1]);
        Assert.Contains(args, a => a.StartsWith("scale=300:300", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("showwavespic", StringComparison.Ordinal));
    }

    [Fact]
    public void Arguments_ForCoverArtOnAShare_ReadOnlyTheStart()
    {
        Assert.Contains("-probesize", AudioArtMaker.Arguments(AudioArtKind.Cover, @"\\nas\m\a.mp3", "o.jpg", 300, 0, isNetwork: true));
        Assert.DoesNotContain("-probesize", AudioArtMaker.Arguments(AudioArtKind.Waveform, @"\\nas\m\a.mp3", "o.jpg", 300, 80, isNetwork: true));
    }

    [Fact]
    public void KeyFor_TellsKindsAndHeightsApart()
    {
        Assert.NotEqual(Key(AudioArtKind.Waveform).Hash, Key(AudioArtKind.Cover).Hash);
        Assert.NotEqual(Key(AudioArtKind.Waveform, height: 100).Hash, Key(AudioArtKind.Waveform, height: 120).Hash);
    }

    [Fact]
    public async Task Make_DrawsOnce_ThenServesTheCache()
    {
        var runner = new FakeFfmpeg(Draws);
        var maker = Maker(runner);
        var key = Key(AudioArtKind.Waveform);

        var first = await maker.MakeAsync(AudioArtKind.Waveform, @"D:\m\song.flac", key, 120, false, TestContext.Current.CancellationToken);
        var second = await maker.MakeAsync(AudioArtKind.Waveform, @"D:\m\song.flac", key, 120, false, TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailStatus.Ok, first.Status);
        Assert.Equal(first.File, second.File);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Make_OfCoverArtThatIsNotThere_SaysSoInPlainWords()
    {
        var runner = new FakeFfmpeg(_ => new ProcessOutcome(1, string.Empty, "Stream map '0:v:0' matches no streams.", false, false));

        var result = await Maker(runner).MakeAsync(AudioArtKind.Cover, @"D:\m\song.flac", Key(AudioArtKind.Cover), 0, false, TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
        Assert.Equal("This file has no cover art.", result.Message);
    }

    [Fact]
    public async Task Make_WhenFfmpegIsMissing_IsToolMissing()
    {
        var result = await Maker(new FakeFfmpeg(Draws), new FfmpegTools(null, null, "none")).MakeAsync(AudioArtKind.Waveform, @"D:\m\a.flac", Key(AudioArtKind.Waveform), 120, false, TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailStatus.ToolMissing, result.Status);
    }

    [Fact]
    public async Task Make_WhenFfmpegCannotBeStarted_IsToolMissing()
    {
        var runner = new FakeFfmpeg(_ => throw new Win32Exception("not found"));

        var result = await Maker(runner).MakeAsync(AudioArtKind.Waveform, @"D:\m\a.flac", Key(AudioArtKind.Waveform), 120, false, TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailStatus.ToolMissing, result.Status);
    }

    [Fact]
    public async Task Make_WhenFfmpegTimesOut_Fails_AndLeavesNoPartFile()
    {
        var runner = new FakeFfmpeg(args =>
        {
            File.WriteAllBytes(args[^1], [1]);
            return new ProcessOutcome(-1, string.Empty, string.Empty, true, false);
        });

        var result = await Maker(runner).MakeAsync(AudioArtKind.Waveform, @"D:\m\a.flac", Key(AudioArtKind.Waveform), 120, false, TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Service_AnswersFromTheCacheWithoutWaitingForItsTurn()
    {
        var runner = new FakeFfmpeg(Draws);
        using var service = new AudioArtService(Maker(runner));
        await service.GetAsync(AudioArtKind.Waveform, @"D:\m\song.flac", 1000, 5, 600, 120, false, TestContext.Current.CancellationToken);

        var again = await service.GetAsync(AudioArtKind.Waveform, @"D:\m\song.flac", 1000, 5, 600, 120, false, TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailStatus.Ok, again.Status);
        Assert.Equal(1, runner.Calls);
    }
}
