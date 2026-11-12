using System.ComponentModel;

namespace Leviathan.Media.Tests;

/// <summary>Perceptual signatures and the look-alike grouping (spec §15.2), over a fake ffmpeg that writes the raw frames itself.</summary>
public sealed class MediaSignatureTests : IDisposable
{
    private static readonly FfmpegTools Found = new(@"C:\ffmpeg\ffprobe.exe", @"C:\ffmpeg\ffmpeg.exe", "setting");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-signature-" + Guid.NewGuid().ToString("N"));

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

    // Writes a ramp of 72 grey bytes to every raw output; ramp + shift changes which neighbours are darker.
    private static ProcessOutcome Draws(IReadOnlyList<string> arguments, Func<int, byte[]>? frame = null)
    {
        var frameNumber = 0;
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == "rawvideo")
            {
                File.WriteAllBytes(arguments[i + 1], frame?.Invoke(frameNumber++) ?? Ramp());
            }
        }

        return new ProcessOutcome(0, string.Empty, string.Empty, false, false);
    }

    private static byte[] Ramp() => [.. Enumerable.Range(0, DifferenceHash.PixelCount).Select(i => (byte)(i % DifferenceHash.Columns * 20))];

    private static MediaFileKey Key(string path = @"D:\p\a.jpg") => new(path, 1000, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

    private MediaSignatureMaker Maker(IProcessRunner runner, FfmpegTools? tools = null, IMediaSignatureStore? store = null) =>
        new(runner, () => tools ?? Found, store ?? new InMemoryMediaSignatureStore(), _folder);

    private static MediaSignature Picture(ulong hash) => new(SignatureKind.Image, [hash], 0);

    private static MediaSignature Video(double seconds, params ulong[] hashes) => new(SignatureKind.Video, hashes, seconds);

    [Fact]
    public void DifferenceHash_IsOneBitForEveryPixelDarkerThanItsRightNeighbour()
    {
        // A ramp that rises to the right sets every bit; one that falls sets none.
        Assert.Equal(ulong.MaxValue, DifferenceHash.FromGrey(Ramp()));
        Assert.Equal(0UL, DifferenceHash.FromGrey([.. Ramp().Reverse()]));
        Assert.Equal(0UL, DifferenceHash.FromGrey(new byte[DifferenceHash.PixelCount]));
    }

    [Fact]
    public void DifferenceHash_NeedsTheWholePicture_AndCountsDifferingBits()
    {
        Assert.Throws<ArgumentException>(() => DifferenceHash.FromGrey(new byte[71]));
        Assert.Equal(0, DifferenceHash.Distance(5, 5));
        Assert.Equal(64, DifferenceHash.Distance(0, ulong.MaxValue));
        Assert.Equal(2, DifferenceHash.Distance(0b1010, 0b0000));
    }

    [Fact]
    public void Arguments_ForAPicture_ShrinkOneFrameToGreyRawBytes()
    {
        var arguments = MediaSignatureMaker.Arguments(@"D:\p\a.jpg", [@"C:\w\0.raw"], [], isNetwork: false);

        Assert.Equal(["-i", @"file:D:\p\a.jpg"], arguments.SkipWhile(a => a != "-i").Take(2));
        Assert.DoesNotContain("-ss", arguments);
        Assert.Contains("scale=9:8:flags=area,format=gray", arguments);
        Assert.Equal("rawvideo", arguments[^2]);
        Assert.Equal(@"C:\w\0.raw", arguments[^1]);
    }

    [Fact]
    public void Arguments_ForAVideo_SeekEachInput_AndWriteOneOutputPerFrame()
    {
        var seeks = MediaSignatureMaker.SeeksFor(TimeSpan.FromSeconds(100));
        var outputs = Enumerable.Range(0, 5).Select(i => $@"C:\w\{i}.raw").ToList();

        var arguments = MediaSignatureMaker.Arguments(@"D:\v\a.mkv", outputs, seeks, isNetwork: true);

        Assert.Equal(["10", "30", "50", "70", "90"], arguments.Where((a, i) => i > 0 && arguments[i - 1] == "-ss"));
        Assert.Equal(5, arguments.Count(a => a == "-i"));
        Assert.Equal(5, arguments.Count(a => a == "-probesize"));
        Assert.Equal(["0:v:0", "1:v:0", "2:v:0", "3:v:0", "4:v:0"], arguments.Where((a, i) => i > 0 && arguments[i - 1] == "-map"));
        Assert.Equal(outputs, arguments.Where((a, i) => i > 0 && arguments[i - 1] == "rawvideo"));
    }

    [Fact]
    public void SeeksFor_StayOffTheLastHalfSecond_OfAShortVideo()
    {
        var seeks = MediaSignatureMaker.SeeksFor(TimeSpan.FromSeconds(1));

        Assert.All(seeks, s => Assert.True(s <= TimeSpan.FromSeconds(0.5)));
        Assert.Equal(5, seeks.Count);
    }

    [Fact]
    public async Task MakeAsync_ForAPicture_HashesTheFrame_KeepsIt_AndLeavesNoRawFileBehind()
    {
        var runner = new FakeFfmpeg(a => Draws(a));
        var store = new InMemoryMediaSignatureStore();
        var maker = Maker(runner, store: store);

        var result = await maker.MakeAsync(Key(), isVideo: false, isNetwork: false, duration: null, CancellationToken.None);
        var again = await maker.MakeAsync(Key(), isVideo: false, isNetwork: false, duration: null, CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Ok, result.Status);
        Assert.Equal([ulong.MaxValue], result.Signature!.Hashes);
        Assert.Equal(SignatureKind.Image, result.Signature.Kind);
        Assert.Single(runner.Calls); // The second answer came from the store.
        Assert.Equal(result.Signature, again.Signature);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task MakeAsync_ForAVideo_TakesFiveFramesInOneRun_AndKeepsTheDuration()
    {
        var runner = new FakeFfmpeg(a => Draws(a, n => n == 2 ? new byte[DifferenceHash.PixelCount] : Ramp()));

        var result = await Maker(runner).MakeAsync(Key(@"D:\v\a.mkv"), isVideo: true, isNetwork: false, TimeSpan.FromSeconds(120), CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Ok, result.Status);
        Assert.Single(runner.Calls);
        Assert.Equal([ulong.MaxValue, ulong.MaxValue, 0UL, ulong.MaxValue, ulong.MaxValue], result.Signature!.Hashes);
        Assert.Equal(120, result.Signature.DurationSeconds);
        Assert.Equal(SignatureKind.Video, result.Signature.Kind);
    }

    [Fact]
    public async Task MakeAsync_WhenFfmpegWritesNoFrame_Fails_WithItsMessage()
    {
        var runner = new FakeFfmpeg(_ => new ProcessOutcome(1, string.Empty, "Invalid data found", false, false));

        var result = await Maker(runner).MakeAsync(Key(), false, false, null, CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
        Assert.Contains("Invalid data", result.Message, StringComparison.Ordinal);
        Assert.Null(result.Signature);
    }

    [Fact]
    public async Task MakeAsync_WhenAFrameIsTheWrongSize_Fails_InsteadOfHashingGarbage()
    {
        var runner = new FakeFfmpeg(a => Draws(a, _ => new byte[10]));

        var result = await Maker(runner).MakeAsync(Key(), false, false, null, CancellationToken.None);

        Assert.Equal(ThumbnailStatus.Failed, result.Status);
    }

    [Fact]
    public async Task MakeAsync_WithoutFfmpeg_OrWithoutADuration_OrOnATimeout_FailsWithoutThrowing()
    {
        var none = new FfmpegTools(null, null, "not found");
        Assert.Equal(ThumbnailStatus.ToolMissing, (await Maker(new FakeFfmpeg(a => Draws(a)), none).MakeAsync(Key(), false, false, null, default)).Status);
        Assert.Equal(ThumbnailStatus.Failed, (await Maker(new FakeFfmpeg(a => Draws(a))).MakeAsync(Key(), true, false, null, default)).Status);
        Assert.Equal(ThumbnailStatus.Failed, (await Maker(new FakeFfmpeg(_ => new ProcessOutcome(-1, string.Empty, string.Empty, true, false))).MakeAsync(Key(), false, false, null, default)).Status);
        Assert.Equal(ThumbnailStatus.ToolMissing, (await Maker(new ThrowingRunner()).MakeAsync(Key(), false, false, null, default)).Status);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessOutcome> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken) =>
            throw new Win32Exception("not an application");
    }

    [Fact]
    public async Task MakeAsync_Cancelled_Throws_AndLeavesNothingBehind()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Maker(new FakeFfmpeg(a => Draws(a))).MakeAsync(Key(), false, false, null, cts.Token));
    }

    [Fact]
    public async Task TheService_MakesOneAtATime_AndCountsMadeAndFailed()
    {
        var running = 0;
        var most = 0;
        var runner = new FakeFfmpeg(a =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            Thread.Sleep(5);
            Interlocked.Decrement(ref running);
            return a.Any(x => x.Contains("bad", StringComparison.Ordinal)) ? new ProcessOutcome(1, string.Empty, "no", false, false) : Draws(a);
        });
        using var service = new MediaSignatureService(Maker(runner));

        var results = await Task.WhenAll(
            service.GetAsync(Key(@"D:\p\1.jpg"), false, false, null, default),
            service.GetAsync(Key(@"D:\p\2.jpg"), false, false, null, default),
            service.GetAsync(Key(@"D:\p\bad.jpg"), false, false, null, default));

        Assert.Equal(1, most);
        Assert.Equal(2, service.Made);
        Assert.Equal(1, service.Failed);
        Assert.Equal(2, results.Count(r => r.Status == ThumbnailStatus.Ok));
    }

    [Fact]
    public void Pictures_AreSimilar_UntilTheyDifferByMoreThanTheTolerance()
    {
        var options = new SimilarityOptions(MaxBitDistance: 6);

        Assert.True(SimilarMediaMatcher.AreSimilar(Picture(0), Picture(0), options));
        Assert.True(SimilarMediaMatcher.AreSimilar(Picture(0), Picture(0b111111), options)); // 6 bits
        Assert.False(SimilarMediaMatcher.AreSimilar(Picture(0), Picture(0b1111111), options)); // 7 bits
    }

    [Fact]
    public void ATolerance_IsKeptToTheOffered_Range()
    {
        Assert.Equal(0, new SimilarityOptions(-5).Distance);
        Assert.Equal(SimilarityOptions.LargestDistance, new SimilarityOptions(500).Distance);
        Assert.False(SimilarMediaMatcher.AreSimilar(Picture(0), Picture(1), new SimilarityOptions(0)));
    }

    [Fact]
    public void APictureAndAVideo_AreNeverSimilar_EvenWithTheSameHash()
    {
        Assert.False(SimilarMediaMatcher.AreSimilar(Picture(5), Video(10, 5), new SimilarityOptions()));
        Assert.False(SimilarMediaMatcher.AreSimilar(Picture(5), new MediaSignature(SignatureKind.Image, [], 0), new SimilarityOptions()));
    }

    [Fact]
    public void Videos_NeedAlikeLengths_AndAlikeFrames()
    {
        var options = new SimilarityOptions(MaxBitDistance: 4, DurationTolerance: 0.03);
        var frames = new ulong[] { 1, 2, 4, 8, 16 };

        Assert.True(SimilarMediaMatcher.AreSimilar(Video(100, frames), Video(102, frames), options)); // 2% longer
        Assert.False(SimilarMediaMatcher.AreSimilar(Video(100, frames), Video(110, frames), options)); // 10% longer
        Assert.True(SimilarMediaMatcher.AreSimilar(Video(0.4, frames), Video(1.3, frames), options)); // Short clips: a second is always allowed.
        Assert.False(SimilarMediaMatcher.AreSimilar(Video(100, frames), Video(100, [1, 2, 4, 8]), options)); // A different number of frames cannot be compared.

        // One frame a different scene altogether (32 bits away) is not forgiven by the other four being identical.
        Assert.False(SimilarMediaMatcher.AreSimilar(Video(100, frames), Video(100, [1, 2, 0xFFFFFFFF, 8, 16]), options));

        // Every frame four bits off is exactly at the tolerance.
        Assert.True(SimilarMediaMatcher.AreSimilar(Video(100, 0, 0, 0, 0, 0), Video(100, 0xF, 0xF, 0xF, 0xF, 0xF), options));
        Assert.False(SimilarMediaMatcher.AreSimilar(Video(100, 0, 0, 0, 0, 0), Video(100, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F), options));
    }

    [Fact]
    public void Group_JoinsChainsOfMatches_KeepsKindsApart_AndLeavesLoneFilesOut()
    {
        var files = new List<(string, MediaSignature)>
        {
            (@"D:\a1.jpg", Picture(0b0000)),
            (@"D:\a2.jpg", Picture(0b0011)),
            (@"D:\lone.jpg", Picture(ulong.MaxValue)),
            (@"D:\a3.jpg", Picture(0b111111)), // 6 bits from a1, 4 from a2
            (@"D:\v1.mkv", Video(100, 0, 0, 0, 0, 0)),
            (@"D:\v2.mkv", Video(101, 1, 0, 0, 0, 0)),
            (@"D:\v3.mkv", Video(500, 0, 0, 0, 0, 0)), // The same frames, a very different length.
        };

        var groups = SimilarMediaMatcher.Group(files, new SimilarityOptions(MaxBitDistance: 6));

        Assert.Equal(2, groups.Count);
        Assert.Equal([@"D:\a1.jpg", @"D:\a2.jpg", @"D:\a3.jpg"], groups[0].Paths);
        Assert.Equal([@"D:\v1.mkv", @"D:\v2.mkv"], groups[1].Paths);
    }

    [Fact]
    public void Group_AgreesWithComparingEveryPair_OnManyRandomPictures()
    {
        var random = new Random(11);
        var files = new List<(string, MediaSignature)>();
        for (var i = 0; i < 400; i++)
        {
            // Clusters of near copies among noise, so there is something to find.
            var basis = (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);
            files.Add(($@"D:\{i}.jpg", Picture(i % 3 == 0 ? (ulong)(i / 30) * 0x0101010101010101UL ^ (1UL << random.Next(64)) : basis)));
        }

        var options = new SimilarityOptions(MaxBitDistance: 5);
        var fast = SimilarMediaMatcher.Group(files, options).Select(g => string.Join('|', g.Paths.Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal).ToList();

        // The reference: connected components of the all-pairs similarity graph.
        var parent = Enumerable.Range(0, files.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        for (var i = 0; i < files.Count; i++)
        {
            for (var j = i + 1; j < files.Count; j++)
            {
                if (SimilarMediaMatcher.AreSimilar(files[i].Item2, files[j].Item2, options))
                {
                    parent[Find(j)] = Find(i);
                }
            }
        }

        var slow = Enumerable.Range(0, files.Count).GroupBy(Find).Where(g => g.Count() > 1)
            .Select(g => string.Join('|', g.Select(i => files[i].Item1).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(slow);
        Assert.Equal(slow, fast);
    }

    [Fact]
    public void Group_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => SimilarMediaMatcher.Group([(@"D:\a.jpg", Picture(1)), (@"D:\b.jpg", Picture(1))], new SimilarityOptions(), cts.Token));
    }

    [Fact]
    public void ImageExtensions_AreRecognisedWithoutRegardToCase()
    {
        Assert.True(MediaFileKinds.IsImage(".JPG"));
        Assert.True(MediaFileKinds.IsImage(".webp"));
        Assert.False(MediaFileKinds.IsImage(".mkv"));
        Assert.False(MediaFileKinds.IsImage(".txt"));
    }
}
