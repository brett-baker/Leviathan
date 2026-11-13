namespace Leviathan.Media.Tests;

/// <summary>The similar-media search around the signature source (spec §15.2).</summary>
public sealed class SimilarMediaFinderTests
{
    private static SimilarCandidate Image(string path) => new(path, 10, 1, IsVideo: false);

    private static MediaSignature Picture(ulong hash) => new(SignatureKind.Image, [hash], 0);

    private static Task<(MediaSignature?, string?)> Answer(MediaSignature? signature, string? problem = null) => Task.FromResult((signature, problem));

    [Fact]
    public async Task FindAsync_GroupsFilesWhoseSignaturesAreClose()
    {
        var hashes = new Dictionary<string, ulong> { [@"C:\a.jpg"] = 0xFF00, [@"C:\b.jpg"] = 0xFF01, [@"C:\c.jpg"] = 0x0123_4567_89AB_CDEFUL };

        var result = await SimilarMediaFinder.FindAsync(
            [.. hashes.Keys.Select(Image)], (c, _) => Answer(Picture(hashes[c.Path])), new SimilarityOptions(6), null, CancellationToken.None);

        var group = Assert.Single(result.Groups);
        Assert.Equal([@"C:\a.jpg", @"C:\b.jpg"], group.Paths.Order());
        Assert.Equal(3, result.Examined);
        Assert.Empty(result.Unreadable);
    }

    [Fact]
    public async Task FindAsync_ATighterToleranceSplitsTheGroup()
    {
        var hashes = new Dictionary<string, ulong> { [@"C:\a.jpg"] = 0x0, [@"C:\b.jpg"] = 0x7F };

        var loose = await SimilarMediaFinder.FindAsync([.. hashes.Keys.Select(Image)], (c, _) => Answer(Picture(hashes[c.Path])), new SimilarityOptions(10), null, CancellationToken.None);
        var tight = await SimilarMediaFinder.FindAsync([.. hashes.Keys.Select(Image)], (c, _) => Answer(Picture(hashes[c.Path])), new SimilarityOptions(2), null, CancellationToken.None);

        Assert.Single(loose.Groups);
        Assert.Empty(tight.Groups);
    }

    [Fact]
    public async Task FindAsync_AFileWithNoSignature_IsReported_AndTheRestAreStillGrouped()
    {
        var result = await SimilarMediaFinder.FindAsync(
            [Image(@"C:\a.jpg"), Image(@"C:\b.jpg"), Image(@"C:\broken.jpg")],
            (c, _) => c.Path.Contains("broken", StringComparison.Ordinal) ? Answer(null, "ffmpeg could not decode it.") : Answer(Picture(5)),
            new SimilarityOptions(6), null, CancellationToken.None);

        Assert.Single(result.Groups);
        var note = Assert.Single(result.Unreadable);
        Assert.Contains("broken.jpg", note, StringComparison.Ordinal);
        Assert.Contains("ffmpeg could not decode it.", note, StringComparison.Ordinal);
        Assert.Equal(2, result.Examined);
    }

    [Fact]
    public async Task FindAsync_AskingForSignatures_NeverExceedsTheParallelismLimit()
    {
        var running = 0;
        var peak = 0;
        var candidates = Enumerable.Range(0, 40).Select(i => Image($@"C:\{i}.jpg")).ToList();

        await SimilarMediaFinder.FindAsync(candidates, async (_, token) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            await Task.Delay(2, token);
            Interlocked.Decrement(ref running);
            return (Picture(1), null);
        }, new SimilarityOptions(6), null, CancellationToken.None);

        Assert.InRange(peak, 1, SimilarMediaFinder.Parallelism);
    }

    [Fact]
    public async Task FindAsync_Cancelling_StopsTheSearch()
    {
        using var cancel = new CancellationTokenSource();
        var asked = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SimilarMediaFinder.FindAsync(
            [.. Enumerable.Range(0, 200).Select(i => Image($@"C:\{i}.jpg"))],
            (_, _) =>
            {
                if (Interlocked.Increment(ref asked) == 5)
                {
                    cancel.Cancel();
                }

                return Answer(Picture(1));
            },
            new SimilarityOptions(6), null, cancel.Token));

        Assert.True(asked < 200);
    }

    [Fact]
    public async Task FindAsync_ReportsEachFileToProgress()
    {
        var seen = new List<string>();
        var progress = new SyncProgress(seen.Add);

        await SimilarMediaFinder.FindAsync([Image(@"C:\a.jpg"), Image(@"C:\b.jpg")], (_, _) => Answer(Picture(1)), new SimilarityOptions(6), progress, CancellationToken.None);

        Assert.Equal([@"C:\a.jpg", @"C:\b.jpg"], seen.Order());
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        private readonly object _gate = new();

        public void Report(string value)
        {
            lock (_gate)
            {
                report(value);
            }
        }
    }
}
