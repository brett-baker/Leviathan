using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

public sealed class ThumbnailCacheTests
{
    private sealed class Source(Func<ThumbnailRequest, CancellationToken, Task<ThumbnailImage?>> create) : IThumbnailSource
    {
        private int _calls;
        private int _running;
        private int _peak;

        public int Calls => Volatile.Read(ref _calls);

        public int Peak => Volatile.Read(ref _peak);

        public async Task<ThumbnailImage?> CreateAsync(ThumbnailRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var running = Interlocked.Increment(ref _running);
            int peak;
            while (running > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, running, peak) != peak)
            {
            }

            try
            {
                return await create(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private static ThumbnailRequest Request(string path, int size = 96, long length = 100, int day = 1) =>
        new(LocationPath.Parse(path), new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), length, size);

    private static ThumbnailImage Image(int bytes = 64) => new(1, 1, new byte[bytes]);

    private static Source Immediate(int bytes = 64) => new((_, _) => Task.FromResult<ThumbnailImage?>(Image(bytes)));

    [Fact]
    public async Task GetAsync_GeneratesOnce_ThenServesFromMemory()
    {
        var source = Immediate();
        using var cache = new ThumbnailCache(source);

        var first = await cache.GetAsync(Request(@"C:\a.jpg"), CancellationToken.None);
        var second = await cache.GetAsync(Request(@"C:\a.jpg"), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, source.Calls);
        Assert.True(cache.TryGetCached(Request(@"C:\a.jpg"), out var hit));
        Assert.Same(first, hit);
    }

    [Fact]
    public async Task GetAsync_ADifferentSizeOrAChangedFile_IsADifferentThumbnail()
    {
        var source = Immediate();
        using var cache = new ThumbnailCache(source);

        await cache.GetAsync(Request(@"C:\a.jpg"), CancellationToken.None);
        await cache.GetAsync(Request(@"C:\a.jpg", size: 256), CancellationToken.None);
        await cache.GetAsync(Request(@"C:\a.jpg", day: 2), CancellationToken.None);
        await cache.GetAsync(Request(@"C:\a.jpg", length: 999), CancellationToken.None);

        Assert.Equal(4, source.Calls);
    }

    [Fact]
    public async Task GetAsync_ConcurrentRequestsForTheSameImage_ShareOneGeneration()
    {
        var release = new TaskCompletionSource();
        var source = new Source(async (_, _) => { await release.Task; return Image(); });
        using var cache = new ThumbnailCache(source);

        var a = cache.GetAsync(Request(@"C:\a.jpg"), CancellationToken.None);
        var b = cache.GetAsync(Request(@"C:\a.jpg"), CancellationToken.None);
        release.SetResult();

        Assert.Same(await a, await b);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task GetAsync_NoThumbnail_IsRememberedAndNotAskedAgain()
    {
        var source = new Source((_, _) => Task.FromResult<ThumbnailImage?>(null));
        using var cache = new ThumbnailCache(source);

        Assert.Null(await cache.GetAsync(Request(@"C:\a.bin"), CancellationToken.None));
        Assert.Null(await cache.GetAsync(Request(@"C:\a.bin"), CancellationToken.None));

        Assert.Equal(1, source.Calls);
        Assert.True(cache.TryGetCached(Request(@"C:\a.bin"), out var image));
        Assert.Null(image);
    }

    [Fact]
    public async Task GetAsync_AFailingFile_ReturnsNull_IsNotRetried_UntilItChanges()
    {
        var source = new Source((r, _) => r.Modified.Day == 1 ? throw new InvalidOperationException("thumbnailer crashed") : Task.FromResult<ThumbnailImage?>(Image()));
        using var cache = new ThumbnailCache(source);

        Assert.Null(await cache.GetAsync(Request(@"C:\bad.mp4"), CancellationToken.None));
        Assert.Null(await cache.GetAsync(Request(@"C:\bad.mp4"), CancellationToken.None));
        Assert.Equal(1, source.Calls);

        Assert.NotNull(await cache.GetAsync(Request(@"C:\bad.mp4", day: 2), CancellationToken.None));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task GetAsync_CancelledBeforeItStarts_NeverRunsTheSource()
    {
        var release = new TaskCompletionSource();
        var source = new Source(async (_, _) => { await release.Task; return Image(); });
        using var cache = new ThumbnailCache(source, maxConcurrent: 1);
        var first = cache.GetAsync(Request(@"C:\one.jpg"), CancellationToken.None);
        await WaitUntil(() => source.Calls == 1);
        using var cts = new CancellationTokenSource();

        var second = cache.GetAsync(Request(@"C:\two.jpg"), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        await first;
        await Task.Delay(50);

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task GetAsync_WhenTheLastWaiterCancels_TheRunningWorkIsCancelled()
    {
        CancellationToken seen = default;
        var started = new TaskCompletionSource();
        var source = new Source(async (_, ct) => { seen = ct; started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Image(); });
        using var cache = new ThumbnailCache(source);
        using var cts = new CancellationTokenSource();

        var pending = cache.GetAsync(Request(@"C:\a.mp4"), cts.Token);
        await started.Task;
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        await WaitUntil(() => seen.IsCancellationRequested);
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task GetAsync_OneWaiterCancelling_DoesNotStopWorkAnotherWaiterNeeds()
    {
        var release = new TaskCompletionSource();
        var source = new Source(async (_, ct) => { await release.Task.WaitAsync(ct); return Image(); });
        using var cache = new ThumbnailCache(source);
        using var cts = new CancellationTokenSource();

        var cancelled = cache.GetAsync(Request(@"C:\a.jpg"), cts.Token);
        var kept = cache.GetAsync(Request(@"C:\a.jpg"), CancellationToken.None);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();

        Assert.NotNull(await kept);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task GetAsync_ConcurrencyIsLimited()
    {
        var source = new Source(async (_, ct) => { await Task.Delay(30, ct); return Image(); });
        using var cache = new ThumbnailCache(source, maxConcurrent: 3);

        var all = Enumerable.Range(0, 20).Select(i => cache.GetAsync(Request(@"C:\f" + i + ".jpg"), CancellationToken.None));
        await Task.WhenAll(all);

        Assert.Equal(20, source.Calls);
        Assert.InRange(source.Peak, 1, 3);
    }

    [Fact]
    public async Task Cache_EvictsLeastRecentlyUsed_WhenOverTheByteBudget()
    {
        using var cache = new ThumbnailCache(Immediate(100), capacityBytes: 250);
        await cache.GetAsync(Request(@"C:\1.jpg"), CancellationToken.None);
        await cache.GetAsync(Request(@"C:\2.jpg"), CancellationToken.None);
        Assert.True(cache.TryGetCached(Request(@"C:\1.jpg"), out _));

        await cache.GetAsync(Request(@"C:\3.jpg"), CancellationToken.None);

        Assert.True(cache.TryGetCached(Request(@"C:\1.jpg"), out _));
        Assert.False(cache.TryGetCached(Request(@"C:\2.jpg"), out _));
        Assert.True(cache.TryGetCached(Request(@"C:\3.jpg"), out _));
        Assert.Equal(200, cache.CachedBytes);
    }

    [Fact]
    public async Task Cache_AnImageLargerThanTheBudget_IsStillReturned()
    {
        using var cache = new ThumbnailCache(Immediate(1000), capacityBytes: 100);

        Assert.NotNull(await cache.GetAsync(Request(@"C:\big.jpg"), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidateFolder_ForgetsThumbnailsAndFailuresBelowIt()
    {
        var calls = 0;
        var source = new Source((_, _) => ++calls == 1 ? throw new InvalidOperationException("boom") : Task.FromResult<ThumbnailImage?>(Image()));
        using var cache = new ThumbnailCache(source);
        await cache.GetAsync(Request(@"C:\Photos\bad.jpg"), CancellationToken.None);
        await cache.GetAsync(Request(@"C:\Photos\ok.jpg"), CancellationToken.None);
        await cache.GetAsync(Request(@"C:\Other\keep.jpg"), CancellationToken.None);

        cache.InvalidateFolder(LocationPath.Parse(@"C:\Photos"));

        Assert.False(cache.TryGetCached(Request(@"C:\Photos\ok.jpg"), out _));
        Assert.True(cache.TryGetCached(Request(@"C:\Other\keep.jpg"), out _));
        Assert.NotNull(await cache.GetAsync(Request(@"C:\Photos\bad.jpg"), CancellationToken.None));
    }

    [Fact]
    public async Task Clear_EmptiesTheCache()
    {
        using var cache = new ThumbnailCache(Immediate());
        await cache.GetAsync(Request(@"C:\a.jpg"), CancellationToken.None);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CachedBytes);
    }

    [Fact]
    public async Task GetAsync_AlreadyCancelled_Throws()
    {
        using var cache = new ThumbnailCache(Immediate());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(Request(@"C:\a.jpg"), cts.Token));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(5);
        }
    }
}
