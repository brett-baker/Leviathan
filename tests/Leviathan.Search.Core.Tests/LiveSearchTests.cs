using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.Search.Core.Tests;

/// <summary>The live search: recursive, parallel, streaming, cancellable (spec §13 "Live search").</summary>
public sealed class LiveSearchTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    /// <summary>Wraps the fake provider to count concurrent listings per share, delay them, or fail some folders.</summary>
    private sealed class WatchingProvider(FakeLocationProvider inner) : ILocationProvider
    {
        private int _active;
        private int _peak;
        private readonly Dictionary<string, int> _activeByShare = [];
        private readonly Dictionary<string, int> _peakByShare = [];
        private readonly Lock _lock = new();

        public TimeSpan Delay { get; init; }

        public HashSet<LocationPath> Failing { get; } = [];

        /// <summary>Listings of folders named "f..." wait (up to 300 ms) until this many are active, so the test sees real parallelism without depending on timing.</summary>
        public int RendezvousAt { get; init; }

        public int Peak => Volatile.Read(ref _peak);

        public int Active => Volatile.Read(ref _active);

        public int PeakFor(string share)
        {
            lock (_lock)
            {
                return _peakByShare.GetValueOrDefault(share);
            }
        }

        public string Name => "watching";

        public bool CanHandle(LocationPath location) => true;

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _active);
            var share = location.Share ?? string.Empty;
            lock (_lock)
            {
                _peak = Math.Max(_peak, now);
                var byShare = _activeByShare.GetValueOrDefault(share) + 1;
                _activeByShare[share] = byShare;
                _peakByShare[share] = Math.Max(_peakByShare.GetValueOrDefault(share), byShare);
            }

            try
            {
                if (RendezvousAt > 0 && location.Name.StartsWith('f'))
                {
                    var waited = System.Diagnostics.Stopwatch.StartNew();
                    while (Volatile.Read(ref _active) < RendezvousAt && waited.Elapsed < TimeSpan.FromMilliseconds(300))
                    {
                        await Task.Delay(1, cancellationToken);
                    }
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                if (Failing.Contains(location))
                {
                    throw new LocationException(LocationError.AccessDenied, location, "Access is denied.");
                }

                await foreach (var batch in inner.ListAsync(location, options, cancellationToken))
                {
                    yield return batch;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                lock (_lock)
                {
                    _activeByShare[share]--;
                }
            }
        }
    }

    private static async Task<(List<FileEntry> Matches, SearchBatch Last)> RunAsync(LiveSearch search, string query, params LocationPath[] roots)
    {
        var matcher = new QueryMatcher(QueryParser.Parse(query), new ManualTimeProvider(), TimeZoneInfo.Utc);
        var matches = new List<FileEntry>();
        SearchBatch? last = null;
        await foreach (var batch in search.RunAsync(roots, matcher, NoSearchFacts.Instance, CancellationToken.None))
        {
            matches.AddRange(batch.Matches);
            last = batch;
        }

        return (matches, last!);
    }

    private static LiveSearch Searcher(ILocationProvider provider, LiveSearchOptions? options = null) =>
        new(new LocationProviderRegistry([provider]), options);

    private static FakeLocationProvider Tree() => new FakeLocationProvider()
        .AddFile(@"C:\root\a.mkv", 10)
        .AddFile(@"C:\root\notes.txt", 5)
        .AddFile(@"C:\root\sub\b.mkv", 20)
        .AddFile(@"C:\root\sub\deeper\c.mkv", 30)
        .AddFile(@"C:\root\other\d.mp3", 40)
        .AddFolder(@"C:\root\empty");

    [Fact]
    public async Task Run_FindsMatchesInEveryLevel()
    {
        var (matches, last) = await RunAsync(Searcher(Tree()), "ext:mkv", P(@"C:\root"));

        Assert.Equal(["a.mkv", "b.mkv", "c.mkv"], matches.Select(m => m.Name).Order());
        Assert.True(last.IsFinal);
        Assert.Equal(3, last.Progress.Matches);
        Assert.Equal(5, last.Progress.FoldersSearched); // root, sub, deeper, other, empty
        Assert.Empty(last.Errors);
    }

    [Fact]
    public async Task Run_MatchesFoldersToo()
    {
        var (matches, _) = await RunAsync(Searcher(Tree()), "type:folder", P(@"C:\root"));

        Assert.Equal(["deeper", "empty", "other", "sub"], matches.Select(m => m.Name).Order());
    }

    [Fact]
    public async Task Run_WithNoMatches_StillEndsWithAFinalBatch()
    {
        var (matches, last) = await RunAsync(Searcher(Tree()), "nothing_called_this", P(@"C:\root"));

        Assert.Empty(matches);
        Assert.True(last.IsFinal);
        Assert.Equal(0, last.Progress.Matches);
        Assert.True(last.Progress.ItemsExamined >= 6);
    }

    [Fact]
    public async Task Run_WithNoRoots_EndsAtOnce()
    {
        var (matches, last) = await RunAsync(Searcher(Tree()), "mkv");

        Assert.Empty(matches);
        Assert.True(last.IsFinal);
    }

    [Fact]
    public async Task Run_SeveralRoots_SearchesAll()
    {
        var provider = Tree().AddFile(@"D:\more\z.mkv", 1);

        var (matches, _) = await RunAsync(Searcher(provider), "ext:mkv", P(@"C:\root\sub"), P(@"D:\more"));

        Assert.Equal(["b.mkv", "c.mkv", "z.mkv"], matches.Select(m => m.Name).Order());
    }

    [Fact]
    public async Task Run_DoesNotFollowLinks()
    {
        var provider = Tree().AddLink(@"C:\root\loop");

        var (matches, last) = await RunAsync(Searcher(provider), "ext:mkv", P(@"C:\root"));

        Assert.Equal(3, matches.Count);
        Assert.Empty(last.Errors);
    }

    [Fact]
    public async Task Run_AFolderThatCannotBeRead_IsReportedAndTheRestIsSearched()
    {
        var watching = new WatchingProvider(Tree());
        watching.Failing.Add(P(@"C:\root\sub"));

        var (matches, last) = await RunAsync(Searcher(watching), "ext:mkv", P(@"C:\root"));

        Assert.Equal(["a.mkv"], matches.Select(m => m.Name));
        var error = Assert.Single(last.Errors);
        Assert.Equal(P(@"C:\root\sub"), error.Folder);
        Assert.Equal(1, last.Progress.ErrorCount);
    }

    [Fact]
    public async Task Run_NoProviderForARoot_IsAFolderErrorNotACrash()
    {
        var search = new LiveSearch(new LocationProviderRegistry([]));

        var (_, last) = await RunAsync(search, "x", P(@"C:\root"));

        Assert.Single(last.Errors);
        Assert.True(last.IsFinal);
    }

    [Fact]
    public async Task Run_BoundsHowManyFoldersAreListedAtOnce()
    {
        var provider = new FakeLocationProvider();
        for (var i = 0; i < 40; i++)
        {
            provider.AddFile($@"C:\wide\f{i}\x.txt", 1);
        }

        var watching = new WatchingProvider(provider) { Delay = TimeSpan.FromMilliseconds(5), RendezvousAt = 4 };

        await RunAsync(Searcher(watching, new LiveSearchOptions { Parallelism = 4 }), "x", P(@"C:\wide"));

        Assert.Equal(4, watching.Peak);
    }

    [Fact]
    public async Task Run_BoundsHowManyFoldersOneShareListsAtOnce()
    {
        var provider = new FakeLocationProvider();
        for (var i = 0; i < 30; i++)
        {
            provider.AddFile($@"\\nas\media\f{i}\x.txt", 1);
        }

        var watching = new WatchingProvider(provider) { Delay = TimeSpan.FromMilliseconds(5), RendezvousAt = 2 };

        await RunAsync(Searcher(watching, new LiveSearchOptions { Parallelism = 8, ParallelismPerShare = 2 }), "x", P(@"\\nas\media"));

        Assert.Equal(2, watching.PeakFor("media"));
    }

    [Fact]
    public async Task Run_SendsLargeResultsInBatchesNoBiggerThanTheLimit()
    {
        var provider = new FakeLocationProvider();
        for (var i = 0; i < 100; i++)
        {
            provider.AddFile($@"C:\many\f{i:D3}.txt", 1);
        }

        var matcher = new QueryMatcher(QueryParser.Parse("txt"), new ManualTimeProvider(), TimeZoneInfo.Utc);
        var sizes = new List<int>();
        await foreach (var batch in Searcher(provider, new LiveSearchOptions { BatchSize = 30 }).RunAsync([P(@"C:\many")], matcher, NoSearchFacts.Instance, CancellationToken.None))
        {
            sizes.Add(batch.Matches.Count);
        }

        Assert.Equal(100, sizes.Sum());
        Assert.All(sizes, s => Assert.True(s <= 30));
    }

    [Fact]
    public async Task Run_Cancelled_EndsWithCancellationNotAFinalBatch()
    {
        var provider = new FakeLocationProvider();
        for (var i = 0; i < 50; i++)
        {
            provider.AddFile($@"C:\slow\f{i}\x.txt", 1);
        }

        var watching = new WatchingProvider(provider) { Delay = TimeSpan.FromMilliseconds(20) };
        var matcher = new QueryMatcher(QueryParser.Parse("x"), new ManualTimeProvider(), TimeZoneInfo.Utc);
        using var cts = new CancellationTokenSource();
        var sawFinal = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in Searcher(watching).RunAsync([P(@"C:\slow")], matcher, NoSearchFacts.Instance, cts.Token))
            {
                sawFinal |= batch.IsFinal;
                await cts.CancelAsync();
            }
        });

        Assert.False(sawFinal);
    }

    [Fact]
    public async Task Run_StoppedEarlyByTheCaller_LeavesNothingListing()
    {
        var provider = new FakeLocationProvider();
        for (var i = 0; i < 50; i++)
        {
            provider.AddFile($@"C:\early\f{i}\x.txt", 1);
        }

        var watching = new WatchingProvider(provider) { Delay = TimeSpan.FromMilliseconds(10) };
        var matcher = new QueryMatcher(QueryParser.Parse("x"), new ManualTimeProvider(), TimeZoneInfo.Utc);

        await foreach (var _ in Searcher(watching).RunAsync([P(@"C:\early")], matcher, NoSearchFacts.Instance, CancellationToken.None))
        {
            break;
        }

        // RunAsync awaits its workers before the loop above ends, so nothing is still listing.
        Assert.Equal(0, watching.Active);
    }
}
