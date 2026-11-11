using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.Search.Core.Tests;

/// <summary>Choosing and merging the three ways of searching (spec §13, D14).</summary>
public sealed class SearchCoordinatorTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class Rig : IDisposable
    {
        public Rig(FakeLocationProvider files)
        {
            Files = files;
            Registry = new LocationProviderRegistry([files]);
            Index = new NameIndex();
            Everything = new FakeEverythingClient();
            Coordinator = new SearchCoordinator(
                new LiveSearch(Registry),
                Everything,
                new EverythingSearch(Everything),
                Index,
                new NameIndexSearch(Index));
        }

        public FakeLocationProvider Files { get; }

        public LocationProviderRegistry Registry { get; }

        public NameIndex Index { get; }

        public FakeEverythingClient Everything { get; }

        public SearchCoordinator Coordinator { get; }

        public async Task CrawlAsync(string root) => await new IndexCrawler(Registry, Index, time: new ManualTimeProvider()).CrawlAsync(P(root), null, CancellationToken.None);

        public async Task<(List<string> Paths, SearchBatch Last)> RunAsync(string text, params string[] roots)
        {
            var query = QueryParser.Parse(text);
            var matcher = new QueryMatcher(query, new ManualTimeProvider(), TimeZoneInfo.Utc);
            var paths = new List<string>();
            SearchBatch? last = null;
            await foreach (var batch in Coordinator.RunAsync(query, [.. roots.Select(P)], matcher, NoSearchFacts.Instance, CancellationToken.None))
            {
                paths.AddRange(batch.Matches.Select(m => m.Location.ToString()));
                last = batch;
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return (paths, last!);
        }

        public void Dispose() => Index.Dispose();
    }

    private static EverythingHit Hit(string path) =>
        new(path, 10, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), FileAttributes.Normal, false);

    private static async Task<IReadOnlyList<SearchSourceChoice>> PlanAsync(Rig rig, string text, params string[] roots) =>
        await rig.Coordinator.PlanAsync(QueryParser.Parse(text), [.. roots.Select(P)], CancellationToken.None);

    [Fact]
    public async Task Plan_WithNothingElse_IsALiveSearch()
    {
        using var rig = new Rig(new FakeLocationProvider().AddFile(@"C:\v\a.mkv", 1));
        rig.Everything.State = EverythingState.NotInstalled;

        var plan = Assert.Single(await PlanAsync(rig, "a", @"C:\v"));

        Assert.Equal(SearchSourceKind.Live, plan.Kind);
        Assert.Null(plan.Note);
    }

    [Fact]
    public async Task Plan_EverythingRunning_TakesLocalDrives_ButNotShares()
    {
        using var rig = new Rig(new FakeLocationProvider());

        var plan = await PlanAsync(rig, "holiday", @"C:\v", @"\\nas\media");

        Assert.Equal([SearchSourceKind.Everything, SearchSourceKind.Live], plan.Select(c => c.Kind));
        Assert.Equal([P(@"C:\v")], plan[0].Roots);
        Assert.Equal([P(@"\\nas\media")], plan[1].Roots);
    }

    [Fact]
    public async Task Plan_EverythingCannotNarrowTheQuery_SoTheOtherSourcesAreUsed()
    {
        using var rig = new Rig(new FakeLocationProvider());

        var plan = Assert.Single(await PlanAsync(rig, "tag:red", @"C:\v"));

        Assert.Equal(SearchSourceKind.Live, plan.Kind);
    }

    [Fact]
    public async Task Plan_EverythingInstalledButNotRunning_SaysSo()
    {
        using var rig = new Rig(new FakeLocationProvider());
        rig.Everything.State = EverythingState.NotRunning;

        var plan = Assert.Single(await PlanAsync(rig, "holiday", @"C:\v"));

        Assert.Equal(SearchSourceKind.Live, plan.Kind);
        Assert.Contains("not running", plan.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_ACrawledShare_IsAnsweredByTheIndex_ButOnlyOnceTheCrawlFinished()
    {
        var files = new FakeLocationProvider().AddFile(@"\\nas\media\inbox\a.mkv", 1);
        using var rig = new Rig(files);
        rig.Everything.State = EverythingState.NotInstalled;

        Assert.Equal(SearchSourceKind.Live, Assert.Single(await PlanAsync(rig, "a", @"\\nas\media\inbox")).Kind);

        await rig.CrawlAsync(@"\\nas\media");

        var plan = Assert.Single(await PlanAsync(rig, "a", @"\\nas\media\inbox"));
        Assert.Equal(SearchSourceKind.Index, plan.Kind);
        Assert.NotNull(plan.IndexedAt);
    }

    [Fact]
    public async Task Plan_ACrawledFolderThatIsNotInTheIndex_IsNotClaimedByIt()
    {
        var files = new FakeLocationProvider().AddFile(@"\\nas\media\inbox\a.mkv", 1);
        using var rig = new Rig(files);
        rig.Everything.State = EverythingState.NotInstalled;
        await rig.CrawlAsync(@"\\nas\media");

        var plan = Assert.Single(await PlanAsync(rig, "a", @"\\nas\other\x"));

        Assert.Equal(SearchSourceKind.Live, plan.Kind);
    }

    [Fact]
    public async Task Run_MergesSourcesAndShowsAnItemFoundTwiceOnce()
    {
        var files = new FakeLocationProvider()
            .AddFile(@"C:\v\holiday1.mp4", 1)
            .AddFile(@"\\nas\media\holiday2.mp4", 1)
            .AddFile(@"\\nas\media\holiday3.mp4", 1);
        using var rig = new Rig(files);
        rig.Everything.Items.Add(Hit(@"C:\v\holiday1.mp4"));
        await rig.CrawlAsync(@"\\nas\media");

        // Everything answers for the local folder, the index for the crawled share; together they list each file once.
        var (paths, last) = await rig.RunAsync("holiday", @"C:\v", @"\\nas\media");

        Assert.Equal([@"C:\v\holiday1.mp4", @"\\nas\media\holiday2.mp4", @"\\nas\media\holiday3.mp4"], paths);
        Assert.Equal([SearchSourceKind.Everything, SearchSourceKind.Index], last.Sources!.Select(s => s.Kind));
        Assert.True(last.IsFinal);
        Assert.Equal(3, last.Progress.Matches);
    }

    [Fact]
    public async Task Run_TheSameFileFromTwoRoots_AppearsOnce()
    {
        using var rig = new Rig(new FakeLocationProvider().AddFile(@"C:\v\sub\a.mp4", 1));
        rig.Everything.State = EverythingState.NotInstalled;

        var (paths, _) = await rig.RunAsync("a.mp4", @"C:\v", @"C:\v\sub");

        Assert.Equal([@"C:\v\sub\a.mp4"], paths);
    }

    [Fact]
    public async Task Run_EverythingStopsAnswering_SearchesTheSameFoldersLive()
    {
        var files = new FakeLocationProvider().AddFile(@"C:\v\holiday.mp4", 1);
        using var rig = new Rig(files);
        rig.Everything.Matches = (_, _) => throw new InvalidOperationException("Everything went away.");
        rig.Everything.Items.Add(Hit(@"C:\v\holiday.mp4"));

        var (paths, last) = await rig.RunAsync("holiday", @"C:\v");

        Assert.Equal([@"C:\v\holiday.mp4"], paths);
        Assert.Equal(SearchSourceKind.Live, Assert.Single(last.Sources!).Kind);
        Assert.Contains("stopped answering", last.Sources![0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Cancelled_Throws_AndFinishesNoSource()
    {
        var files = new FakeLocationProvider();
        for (var i = 0; i < 30; i++)
        {
            files.AddFile($@"C:\big\f{i}\x.txt", 1);
        }

        using var rig = new Rig(files);
        rig.Everything.State = EverythingState.NotInstalled;
        var query = QueryParser.Parse("x");
        using var cts = new CancellationTokenSource();
        var sawFinal = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in rig.Coordinator.RunAsync(query, [P(@"C:\big")], new QueryMatcher(query), NoSearchFacts.Instance, cts.Token))
            {
                sawFinal |= batch.IsFinal;
                await cts.CancelAsync();
            }
        });

        Assert.False(sawFinal);
    }

    [Fact]
    public async Task Run_UnreadableFolders_AreReportedOnTheFinalBatch()
    {
        using var rig = new Rig(new FakeLocationProvider().AddFile(@"C:\v\a.txt", 1));
        rig.Everything.State = EverythingState.NotInstalled;

        var (_, last) = await rig.RunAsync("a", @"C:\v", @"C:\missing");

        Assert.Single(last.Errors);
    }
}
