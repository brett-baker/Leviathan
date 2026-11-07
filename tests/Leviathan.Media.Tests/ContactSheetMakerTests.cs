namespace Leviathan.Media.Tests;

public sealed class ContactSheetMakerTests : IDisposable
{
    private static readonly FfmpegTools Found = new(@"C:\ffmpeg\ffprobe.exe", @"C:\ffmpeg\ffmpeg.exe", "setting");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-sheets-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeFfmpeg(Func<IReadOnlyList<string>, ProcessOutcome> behave) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments);
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

    private ContactSheetMaker Maker(FakeFfmpeg ffmpeg, FfmpegTools? tools = null) => new(ffmpeg, () => tools ?? Found, new ThumbnailDiskCache(_folder));

    private static ContactSheetSpec Spec(int columns = 3, int rows = 3) => ContactSheetSpec.For(columns, rows, 240, 1920, 1080);

    [Fact]
    public void Spec_KeepsTheGridTo3To6_TheCellWidthTo96To480_AndTheCellsInTheVideosProportions()
    {
        var big = ContactSheetSpec.For(9, 1, 5000, 1920, 1080);
        Assert.Equal((6, 3, 480, 270), (big.Columns, big.Rows, big.CellWidth, big.CellHeight));

        var small = ContactSheetSpec.For(4, 4, 10, 1920, 1080);
        Assert.Equal((96, 54), (small.CellWidth, small.CellHeight));

        Assert.Equal(426, ContactSheetSpec.For(3, 3, 240, 1080, 1920).CellHeight);
        Assert.Equal(136, ContactSheetSpec.For(3, 3, 240, null, null).CellHeight);
        Assert.Equal(240 * 3, ContactSheetSpec.For(3, 3, 240, 100, 10000).CellHeight);
    }

    [Fact]
    public void Spec_SizesAreEven_AndTheWholeSheetIsTheGrid()
    {
        var spec = ContactSheetSpec.For(5, 4, 241, 1000, 563);

        Assert.Equal(0, spec.CellWidth % 2);
        Assert.Equal(0, spec.CellHeight % 2);
        Assert.Equal((spec.Columns * spec.CellWidth, spec.Rows * spec.CellHeight, 20), (spec.Width, spec.Height, spec.Count));
    }

    [Fact]
    public void FrameTimes_AreTheMiddleOfEqualPartsOfTheFirst95Percent_InRowOrder()
    {
        var times = Spec().FrameTimes(TimeSpan.FromSeconds(90));

        Assert.Equal(9, times.Count);
        Assert.Equal(4.75, times[0].TotalSeconds, 6);
        Assert.Equal(80.75, times[8].TotalSeconds, 6);
        Assert.Equal(times.OrderBy(t => t), times);
    }

    [Fact]
    public void FrameTimes_OfAVeryShortVideo_NeverGoIntoTheLastHalfSecond()
    {
        var times = Spec().FrameTimes(TimeSpan.FromSeconds(1));

        Assert.All(times, t => Assert.True(t.TotalSeconds <= 0.5 + 1e-9));
        Assert.All(Spec().FrameTimes(TimeSpan.FromSeconds(0.2)), t => Assert.Equal(0, t.TotalSeconds));
    }

    [Fact]
    public void Arguments_SeekEveryFrameBeforeItsInput_StackThemIntoTheGrid_AndWriteOneJpeg()
    {
        var spec = Spec();
        var times = spec.FrameTimes(TimeSpan.FromSeconds(90));

        var arguments = ContactSheetMaker.Arguments(@"D:\v\clip.mkv", @"D:\c\sheet.jpg", spec, times, isNetwork: false);

        Assert.Equal(9, arguments.Count(a => a == "-ss"));
        Assert.Equal(9, arguments.Count(a => a == "file:D:\\v\\clip.mkv"));
        var seeks = arguments.Select((a, i) => (a, i)).Where(x => x.a == "-ss").Select(x => x.i).ToList();
        Assert.All(seeks, i => Assert.Equal("-i", arguments[i + 2]));
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("xstack=inputs=9:layout=0_0|240_0|480_0|0_136|240_136|480_136|0_272|240_272|480_272[sheet]", graph);
        Assert.Contains("scale=240:136:force_original_aspect_ratio=decrease,pad=240:136", graph);
        Assert.Equal(@"D:\c\sheet.jpg", arguments[^1]);
        Assert.DoesNotContain("-probesize", arguments);
        Assert.Contains("-probesize", ContactSheetMaker.Arguments(@"\\nas\a.mp4", @"D:\c\s.jpg", spec, times, isNetwork: true));
    }

    [Fact]
    public async Task Make_DrawsOnce_ThenServesTheSameSheetFromTheCache_WithTheFrameTimes()
    {
        var ffmpeg = new FakeFfmpeg(Draws);
        var maker = Maker(ffmpeg);

        var first = await maker.MakeAsync(@"D:\v\clip.mkv", 1000, 5, false, TimeSpan.FromSeconds(90), Spec(), CancellationToken.None);
        var second = await maker.MakeAsync(@"D:\v\clip.mkv", 1000, 5, false, TimeSpan.FromSeconds(90), Spec(), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Ok, first.Status);
        Assert.Equal(first.File, second.File);
        Assert.Single(ffmpeg.Calls);
        Assert.Equal(9, second.Times.Count);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.part.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Make_ADifferentGridOrAnEditedFile_IsADifferentSheet()
    {
        var ffmpeg = new FakeFfmpeg(Draws);
        var maker = Maker(ffmpeg);
        var length = TimeSpan.FromSeconds(90);

        var a = await maker.MakeAsync(@"D:\v\clip.mkv", 1000, 5, false, length, Spec(), CancellationToken.None);
        var grid = await maker.MakeAsync(@"D:\v\clip.mkv", 1000, 5, false, length, Spec(4, 4), CancellationToken.None);
        var edited = await maker.MakeAsync(@"D:\v\clip.mkv", 1000, 6, false, length, Spec(), CancellationToken.None);

        Assert.Equal(3, new[] { a.File, grid.File, edited.File }.Distinct().Count());
        Assert.Equal(3, ffmpeg.Calls.Count);
    }

    [Fact]
    public async Task Make_WithoutADuration_IsFailedAndRunsNothing()
    {
        var ffmpeg = new FakeFfmpeg(Draws);

        var result = await Maker(ffmpeg).MakeAsync(@"D:\v\clip.mkv", 1000, 5, false, null, Spec(), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task Make_WithoutFfmpeg_IsToolMissing_AndWhenFfmpegFails_IsFailedWithItsReason_AndLeavesNoFile()
    {
        var missing = Maker(new FakeFfmpeg(Draws), new FfmpegTools(null, null, "not found"));
        Assert.Equal(ThumbnailStatus.ToolMissing, (await missing.MakeAsync(@"D:\v\a.mkv", 1, 1, false, TimeSpan.FromMinutes(1), Spec(), CancellationToken.None)).Status);

        var failing = Maker(new FakeFfmpeg(a =>
        {
            File.WriteAllBytes(a[^1], [1]);
            return new ProcessOutcome(1, string.Empty, "Invalid data found", false, false);
        }));
        var result = await failing.MakeAsync(@"D:\v\b.mkv", 1, 1, false, TimeSpan.FromMinutes(1), Spec(), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
        Assert.Equal("Invalid data found", result.Message);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Service_DrawsOneAtATime_AndACallerThatLeavesWhileWaitingNeverStarts()
    {
        var gate = new TaskCompletionSource();
        var started = new List<string>();
        var runner = new GatedRunner(gate.Task, a =>
        {
            lock (started)
            {
                started.Add(a[a.ToList().IndexOf("-i") + 1]);
            }
        });
        using var service = new ContactSheetService(Maker2(runner));

        var first = service.GetAsync(@"D:\v\first.mkv", 1, 1, false, TimeSpan.FromMinutes(1), Spec(), CancellationToken.None);
        using var leaving = new CancellationTokenSource();
        var left = service.GetAsync(@"D:\v\left.mkv", 1, 1, false, TimeSpan.FromMinutes(1), Spec(), leaving.Token);
        await WaitUntil(() => service.Running == 1 && service.Waiting == 1);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => left);
        gate.SetResult();
        await first;

        Assert.Equal(1, service.Made);
        lock (started)
        {
            Assert.DoesNotContain(started, s => s.Contains("left.mkv", StringComparison.Ordinal));
        }
    }

    private ContactSheetMaker Maker2(IProcessRunner runner) => new(runner, () => Found, new ThumbnailDiskCache(_folder));

    private sealed class GatedRunner(Task gate, Action<IReadOnlyList<string>> entered) : IProcessRunner
    {
        public async Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
        {
            entered(arguments);
            await gate.WaitAsync(cancellationToken);
            return Draws(arguments);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met within 10 s.");
            await Task.Delay(5);
        }
    }
}

public sealed class RealContactSheetTests(MediaFixtures fixtures) : IClassFixture<MediaFixtures>
{
    [Fact]
    public async Task Make_FromAGeneratedClip_WithAnOddName_WritesOneJpegAsBigAsTheGrid()
    {
        Assert.SkipUnless(fixtures.Tools.FfmpegPath is not null, "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var maker = new ContactSheetMaker(new SystemProcessRunner(), () => fixtures.Tools, new ThumbnailDiskCache(Path.Combine(fixtures.Folder, "sheets")));
        var spec = ContactSheetSpec.For(3, 3, 96, 64, 48);

        var result = await maker.MakeAsync(fixtures.Clip, new FileInfo(fixtures.Clip).Length, File.GetLastWriteTimeUtc(fixtures.Clip).Ticks, false, TimeSpan.FromSeconds(2), spec, CancellationToken.None);

        Assert.True(result.Status == ThumbnailStatus.Ok, result.Message);
        var bytes = await File.ReadAllBytesAsync(result.File!);
        Assert.Equal([(byte)0xFF, (byte)0xD8], bytes.Take(2));
        var probe = await new FfprobeRunner(new SystemProcessRunner(), () => fixtures.Tools).ProbeAsync(result.File!, false, CancellationToken.None);
        var info = FfprobeParser.TryParse(probe.Json!);
        Assert.Equal((spec.Width, spec.Height), (info!.PrimaryVideo!.Width, info.PrimaryVideo.Height));
        Assert.Equal(9, result.Times.Count);
    }

    [Fact]
    public async Task Make_FromAnEmptyFile_IsFailedNotAnException()
    {
        Assert.SkipUnless(fixtures.Tools.FfmpegPath is not null, "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var maker = new ContactSheetMaker(new SystemProcessRunner(), () => fixtures.Tools, new ThumbnailDiskCache(Path.Combine(fixtures.Folder, "sheets2")));

        var result = await maker.MakeAsync(fixtures.Empty, 0, 1, false, TimeSpan.FromSeconds(10), ContactSheetSpec.For(3, 3, 96, null, null), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
    }
}
