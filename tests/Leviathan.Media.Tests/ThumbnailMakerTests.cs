using System.ComponentModel;

namespace Leviathan.Media.Tests;

/// <summary>The thumbnail maker and its disk cache, over a fake ffmpeg that writes the output file itself (real temp folders, no real ffmpeg).</summary>
public sealed class ThumbnailMakerTests : IDisposable
{
    private static readonly FfmpegTools Found = new(@"C:\ffmpeg\ffprobe.exe", @"C:\ffmpeg\ffmpeg.exe", "setting");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-thumbs-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeFfmpeg(Func<IReadOnlyList<string>, ProcessOutcome> behave) : IProcessRunner
    {
        private readonly List<IReadOnlyList<string>> _calls = [];

        public IReadOnlyList<IReadOnlyList<string>> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_calls)
            {
                _calls.Add(arguments);
            }

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

    // Behaves like ffmpeg: writes a few bytes to the last argument (the output file).
    private static ProcessOutcome Draws(IReadOnlyList<string> arguments)
    {
        File.WriteAllBytes(arguments[^1], [0xFF, 0xD8, 1, 2, 3]);
        return new ProcessOutcome(0, string.Empty, string.Empty, false, false);
    }

    private static ThumbnailKey Key(string path = @"D:\v\clip.mkv", int edge = 128) => new(path, 1000, 5, edge);

    private ThumbnailDiskCache Cache(long capacity = ThumbnailDiskCache.DefaultCapacityBytes) => new(_folder, capacity);

    [Fact]
    public void SeekFor_IsATenthOfTheWayIn_Between1SecondAnd10Minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), ThumbnailMaker.SeekFor(TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(1), ThumbnailMaker.SeekFor(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromMinutes(10), ThumbnailMaker.SeekFor(TimeSpan.FromHours(10)));
        Assert.Equal(TimeSpan.FromSeconds(0.5), ThumbnailMaker.SeekFor(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(ThumbnailMaker.DefaultSeek, ThumbnailMaker.SeekFor(null));
    }

    [Fact]
    public void Arguments_SeekBeforeTheInput_ReadTheFileAsAFile_AndScaleToTheEdge()
    {
        var arguments = ThumbnailMaker.Arguments(@"D:\v\clip.mkv", @"D:\c\a.jpg", TimeSpan.FromSeconds(12.5), 288, isNetwork: false);

        Assert.True(arguments.ToList().IndexOf("-ss") < arguments.ToList().IndexOf("-i"));
        Assert.Equal("12.5", arguments[arguments.ToList().IndexOf("-ss") + 1]);
        Assert.Equal("file:D:\\v\\clip.mkv", arguments[arguments.ToList().IndexOf("-i") + 1]);
        Assert.Contains("scale=288:288:force_original_aspect_ratio=decrease", arguments);
        Assert.Equal(@"D:\c\a.jpg", arguments[^1]);
        Assert.DoesNotContain("-probesize", arguments);
        Assert.Contains("-probesize", ThumbnailMaker.Arguments(@"\\nas\a.mp4", @"D:\c\a.jpg", TimeSpan.Zero, 96, isNetwork: true));
    }

    [Fact]
    public async Task Make_DrawsOnce_ThenServesTheSameFileFromTheCache()
    {
        var ffmpeg = new FakeFfmpeg(Draws);
        var maker = new ThumbnailMaker(ffmpeg, () => Found, Cache());

        var first = await maker.MakeAsync(Key(), false, TimeSpan.FromSeconds(100), CancellationToken.None);
        var second = await maker.MakeAsync(Key(), false, TimeSpan.FromSeconds(100), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Ok, first.Status);
        Assert.Equal(first.File, second.File);
        Assert.Single(ffmpeg.Calls);
        Assert.True(File.Exists(first.File));
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.part.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Make_AnEditedFileOrAnotherSize_IsADifferentThumbnail()
    {
        var ffmpeg = new FakeFfmpeg(Draws);
        var maker = new ThumbnailMaker(ffmpeg, () => Found, Cache());

        var a = await maker.MakeAsync(Key(), false, null, CancellationToken.None);
        var edited = await maker.MakeAsync(Key() with { ModifiedUtcTicks = 6 }, false, null, CancellationToken.None);
        var bigger = await maker.MakeAsync(Key(edge: 256), false, null, CancellationToken.None);

        Assert.Equal(3, new[] { a.File, edited.File, bigger.File }.Distinct().Count());
        Assert.Equal(3, ffmpeg.Calls.Count);
    }

    [Fact]
    public async Task Make_WhenTheSeekDrawsNothing_TriesFromTheStart_ThenGivesUpWithFfmpegsReason()
    {
        var ffmpeg = new FakeFfmpeg(_ => new ProcessOutcome(0, string.Empty, "moov atom not found", false, false));
        var maker = new ThumbnailMaker(ffmpeg, () => Found, Cache());

        var result = await maker.MakeAsync(Key(), false, TimeSpan.FromSeconds(100), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
        Assert.Equal("moov atom not found", result.Message);
        Assert.Equal(2, ffmpeg.Calls.Count);
        Assert.Equal("0", ffmpeg.Calls[1][ffmpeg.Calls[1].ToList().IndexOf("-ss") + 1]);
        Assert.Null(Cache().TryGet(Key()));
    }

    [Fact]
    public async Task Make_WhenTheSeekIsPastTheEnd_TheSecondTryFromTheStartSucceeds()
    {
        var ffmpeg = new FakeFfmpeg(a => a[a.ToList().IndexOf("-ss") + 1] == "0" ? Draws(a) : new ProcessOutcome(0, string.Empty, string.Empty, false, false));
        var maker = new ThumbnailMaker(ffmpeg, () => Found, Cache());

        var result = await maker.MakeAsync(Key(), false, TimeSpan.FromSeconds(100), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Make_WithoutFfmpeg_IsToolMissing_AndRunsNothing()
    {
        var ffmpeg = new FakeFfmpeg(Draws);
        var maker = new ThumbnailMaker(ffmpeg, () => new FfmpegTools(null, null, "not found"), Cache());

        Assert.Equal(ThumbnailStatus.ToolMissing, (await maker.MakeAsync(Key(), false, null, CancellationToken.None)).Status);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task Make_WhenFfmpegCannotBeStarted_IsToolMissing()
    {
        var maker = new ThumbnailMaker(new ThrowingRunner(), () => Found, Cache());

        Assert.Equal(ThumbnailStatus.ToolMissing, (await maker.MakeAsync(Key(), false, null, CancellationToken.None)).Status);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken) =>
            throw new Win32Exception("not found");
    }

    [Fact]
    public async Task Make_WhenFfmpegTimesOut_IsFailed_WithoutALeftoverFile()
    {
        var ffmpeg = new FakeFfmpeg(a =>
        {
            File.WriteAllBytes(a[^1], [1]);
            return new ProcessOutcome(-1, string.Empty, string.Empty, true, false);
        });
        var maker = new ThumbnailMaker(ffmpeg, () => Found, Cache());

        var result = await maker.MakeAsync(Key(), false, null, CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void Trim_RemovesTheLeastRecentlyUsedFirst_UntilUnderTheCap()
    {
        var cache = Cache(capacity: 100);
        var keys = Enumerable.Range(0, 5).Select(i => Key($@"D:\v\{i}.mkv")).ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            var file = cache.PathFor(keys[i]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[40]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10 + i));
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow.AddMinutes(-10 + i));
        }

        // The oldest one is used again: it is now the freshest and must survive.
        Assert.NotNull(cache.TryGet(keys[0]));
        cache.Trim();

        Assert.NotNull(cache.TryGet(keys[0]));
        Assert.Null(cache.TryGet(keys[1]));
        Assert.Null(cache.TryGet(keys[2]));
        Assert.Null(cache.TryGet(keys[3]));
        Assert.NotNull(cache.TryGet(keys[4]));
    }

    [Fact]
    public async Task Service_DrawsTwoAtATime_AndACallerThatLeavesWhileWaitingNeverStarts()
    {
        var gate = new TaskCompletionSource();
        var running = 0;
        var peak = 0;
        var ffmpeg = new GatedRunner(gate.Task, () =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }
        }, () => Interlocked.Decrement(ref running));
        using var service = new ThumbnailService(new ThumbnailMaker(ffmpeg, () => Found, Cache()), maxParallel: 2);

        var tasks = Enumerable.Range(0, 3).Select(i => service.GetAsync(Key($@"D:\v\{i}.mkv"), false, null, CancellationToken.None)).ToList();
        using var leaving = new CancellationTokenSource();
        var left = service.GetAsync(Key(@"D:\v\left.mkv"), false, null, leaving.Token);
        await WaitUntil(() => service.Running == 2);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => left);
        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(2, Volatile.Read(ref peak));
        Assert.Equal(3, service.Made);
        Assert.DoesNotContain(ffmpeg.Paths, p => p.Contains("left.mkv", StringComparison.Ordinal));
    }

    private sealed class GatedRunner(Task gate, Action entered, Action left) : IProcessRunner
    {
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        public async Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
        {
            lock (_paths)
            {
                _paths.Add(arguments[arguments.ToList().IndexOf("-i") + 1]);
            }

            entered();
            try
            {
                await gate.WaitAsync(cancellationToken);
                return Draws(arguments);
            }
            finally
            {
                left();
            }
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

public sealed class RealThumbnailTests(MediaFixtures fixtures) : IClassFixture<MediaFixtures>
{
    [Fact]
    public async Task Make_FromAGeneratedClip_WithAnOddName_WritesARealJpegWithinTheEdge()
    {
        Assert.SkipUnless(fixtures.Tools.FfmpegPath is not null, "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var folder = Path.Combine(fixtures.Folder, "thumbs");
        var maker = new ThumbnailMaker(new SystemProcessRunner(), () => fixtures.Tools, new ThumbnailDiskCache(folder));
        var key = new ThumbnailKey(fixtures.Clip, new FileInfo(fixtures.Clip).Length, File.GetLastWriteTimeUtc(fixtures.Clip).Ticks, 32);

        var result = await maker.MakeAsync(key, false, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Ok, result.Status);
        var bytes = await File.ReadAllBytesAsync(result.File!);
        Assert.Equal([(byte)0xFF, (byte)0xD8], bytes.Take(2));
        Assert.Equal([(byte)0xFF, (byte)0xD9], bytes.TakeLast(2));
    }

    [Fact]
    public async Task Make_FromAnEmptyFileOrATextFile_IsFailedNotAnException()
    {
        Assert.SkipUnless(fixtures.Tools.FfmpegPath is not null, "ffmpeg is not installed here (set LEVIATHAN_FFMPEG_DIR or add it to PATH).");
        var maker = new ThumbnailMaker(new SystemProcessRunner(), () => fixtures.Tools, new ThumbnailDiskCache(Path.Combine(fixtures.Folder, "thumbs2")));

        foreach (var path in new[] { fixtures.Text, fixtures.Empty })
        {
            var result = await maker.MakeAsync(new ThumbnailKey(path, new FileInfo(path).Length, 1, 64), false, null, CancellationToken.None);
            Assert.Equal(ThumbnailStatus.Failed, result.Status);
        }
    }
}
