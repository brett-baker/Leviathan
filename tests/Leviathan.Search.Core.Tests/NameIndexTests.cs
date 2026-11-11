using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.Search.Core.Tests;

/// <summary>The in-memory name index, its crawler, and that searching it agrees with the live search (spec §13).</summary>
public sealed class NameIndexTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static IndexedChild File(string name, long size = 1, long modified = 100) => new(name, false, size, modified, FileAttributes.Normal);

    private static IndexedChild Dir(string name) => new(name, true, 0, 100, FileAttributes.Directory);

    private static List<string> Find(NameIndex index, string literal, params string[] roots) =>
        [.. index.Candidates(literal, [.. roots.Select(P)], null, int.MaxValue, CancellationToken.None).Select(e => e.Location.ToString()).Order(StringComparer.Ordinal)];

    [Fact]
    public void ReplaceChildren_AddsEntries_AndFindsThemByPath()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        var ids = index.ReplaceChildren(root, [Dir("Videos"), File("a.txt")]);
        index.ReplaceChildren(ids[0], [File("holiday.mp4", 500)]);

        Assert.Equal(3, index.Count);
        Assert.Equal(ids[0], index.Find(P(@"C:\videos")));
        Assert.Equal(P(@"C:\Videos\holiday.mp4"), index.PathOf(index.Find(P(@"C:\Videos\HOLIDAY.mp4"))));
        Assert.Equal(-1, index.Find(P(@"C:\Videos\nothing")));
        Assert.Equal(-1, index.Find(P(@"D:\Videos")));
    }

    [Fact]
    public void AddRoot_Twice_IsTheSameRoot()
    {
        using var index = new NameIndex();

        Assert.Equal(index.AddRoot(P(@"\\nas\media")), index.AddRoot(P(@"\\NAS\Media")));
        Assert.Single(index.Roots);
    }

    [Fact]
    public void ReplaceChildren_KeepsTheIdOfWhatIsStillThere_AndUpdatesIt()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        var first = index.ReplaceChildren(root, [File("a.txt", 1), Dir("sub")]);
        index.ReplaceChildren(first[1], [File("deep.txt")]);

        var second = index.ReplaceChildren(root, [File("a.txt", 99), Dir("sub")]);

        Assert.Equal(first, second);
        Assert.Equal(99, index.Describe(second[0]).Size);
        Assert.Equal(3, index.Count); // the folder's content survived
    }

    [Fact]
    public void ReplaceChildren_RemovesWhatIsGone_WithEverythingBelowIt()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        var ids = index.ReplaceChildren(root, [Dir("old"), File("keep.txt")]);
        index.ReplaceChildren(ids[0], [File("inside.txt")]);

        index.ReplaceChildren(root, [File("keep.txt")]);

        Assert.Equal(1, index.Count);
        Assert.Empty(Find(index, "inside"));
        Assert.Equal(-1, index.Find(P(@"C:\old")));
    }

    [Fact]
    public void ReplaceChildren_AListingThatLeavesOutHiddenItems_KeepsTheHiddenOnesTheIndexKnows()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        index.ReplaceChildren(root, [File("shown.txt"), new IndexedChild("secret.txt", false, 1, 1, FileAttributes.Hidden), File("gone.txt")]);

        // The folder as a person sees it: no hidden items, and gone.txt has been deleted.
        index.ReplaceChildren(root, [File("shown.txt")], listedHiddenAndSystem: false);

        Assert.Equal([@"C:\secret.txt", @"C:\shown.txt"], Find(index, ".txt"));
        Assert.Equal(2, index.ChildrenOf(root).Count);

        index.ReplaceChildren(root, [File("shown.txt")]); // a full listing removes it
        Assert.Equal([@"C:\shown.txt"], Find(index, ".txt"));
    }

    [Fact]
    public void ReplaceChildren_ACaseOnlyRename_ShowsTheNewCase()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        index.ReplaceChildren(root, [File("report.txt")]);

        index.ReplaceChildren(root, [File("Report.txt")]);

        Assert.Equal([@"C:\Report.txt"], Find(index, "report"));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void ReplaceChildren_AFileBecomingAFolder_IsANewEntry()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        var before = index.ReplaceChildren(root, [File("thing")]);

        var after = index.ReplaceChildren(root, [Dir("thing")]);

        Assert.NotEqual(before[0], after[0]);
        Assert.True(index.Describe(after[0]).IsFolder);
    }

    [Fact]
    public void Remove_TakesTheWholeSubtree_AndARootWithItsTree()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        var ids = index.ReplaceChildren(root, [Dir("a"), Dir("b")]);
        index.ReplaceChildren(ids[0], [File("x.txt")]);

        index.Remove(ids[0]);
        Assert.Equal(1, index.Count);
        Assert.Empty(Find(index, "x.txt"));

        index.Remove(root);
        Assert.Equal(0, index.Count);
        Assert.Empty(index.Roots);
    }

    [Fact]
    public void Candidates_FindsTheWordAnywhereInAName_IgnoringCase_OnlyUnderTheRoots()
    {
        using var index = new NameIndex();
        var c = index.AddRoot(P(@"C:\"));
        var d = index.AddRoot(P(@"D:\"));
        index.ReplaceChildren(c, [File("Summer HOLIDAY.mp4"), File("holiday2.mp4"), File("other.txt")]);
        index.ReplaceChildren(d, [File("holiday.mkv")]);

        Assert.Equal([@"C:\Summer HOLIDAY.mp4", @"C:\holiday2.mp4", @"D:\holiday.mkv"], Find(index, "holiday"));
        Assert.Equal([@"C:\Summer HOLIDAY.mp4", @"C:\holiday2.mp4"], Find(index, "holiday", @"C:\"));
        Assert.Empty(Find(index, "holiday", @"E:\"));
    }

    [Fact]
    public void Candidates_ARootsOwnName_IsNeverAResult()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\Videos"));
        index.ReplaceChildren(root, [File("a.mp4")]);

        Assert.Equal([@"C:\Videos\a.mp4"], Find(index, "a.mp4"));
        Assert.Empty(Find(index, "Videos")); // the path above the root is not a name in the index
    }

    [Fact]
    public void Candidates_ASearchWordCannotMatchAcrossTwoNames()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        index.ReplaceChildren(root, [File("abc"), File("def")]);

        Assert.Empty(Find(index, "cd"));
        Assert.Empty(Find(index, "abcdef"));
    }

    [Fact]
    public void Candidates_WithoutAWord_ListsEverything_AndKeepDropsByNameAndKind()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        index.ReplaceChildren(root, [File("a.txt"), Dir("sub"), File("b.mkv", 5000)]);

        var all = index.Candidates(null, [], null, int.MaxValue, CancellationToken.None);
        var big = index.Candidates(null, [], c => !c.IsFolder && c.Size > 1000, int.MaxValue, CancellationToken.None);

        Assert.Equal(3, all.Count);
        Assert.Equal([@"C:\b.mkv"], big.Select(e => e.Location.ToString()));
    }

    [Fact]
    public void Candidates_StopAtTheLimit()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        index.ReplaceChildren(root, [.. Enumerable.Range(0, 100).Select(i => File($"file{i}.txt"))]);

        Assert.Equal(10, index.Candidates("file", [], null, 10, CancellationToken.None).Count);
    }

    [Theory]
    [InlineData("file")]
    [InlineData(null)]
    public void CandidatePage_WalksThePagesWithoutRepeatsOrGaps_AndSeesWhatIsAddedBetweenPages(string? literal)
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        index.ReplaceChildren(root, [.. Enumerable.Range(0, 100).Select(i => File($"file{i:D3}.txt"))]);
        var cursor = new IndexCursor();
        var seen = new List<string>();

        var first = index.CandidatePage(literal, [], null, ref cursor, 30, CancellationToken.None);
        seen.AddRange(first.Select(e => e.Name));
        index.ReplaceChildren(root, [.. Enumerable.Range(0, 100).Select(i => File($"file{i:D3}.txt")), File("file_late.txt")]);
        while (!cursor.IsDone)
        {
            seen.AddRange(index.CandidatePage(literal, [], null, ref cursor, 30, CancellationToken.None).Select(e => e.Name));
        }

        Assert.Equal(30, first.Count);
        Assert.Equal(101, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("file_late.txt", seen);
    }

    [Fact]
    public void Candidates_Cancelled_Throws()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        index.ReplaceChildren(root, [File("a.txt")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => index.Candidates("a", [], null, 10, cts.Token));
    }

    [Fact]
    public void Candidates_BuildTheEntryWithSizeKindAndTime()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        var modified = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc).Ticks;
        index.ReplaceChildren(root, [new IndexedChild("movie.mkv", false, 1234, modified, FileAttributes.Hidden)]);

        var entry = Assert.Single(index.Candidates("movie", [], null, 10, CancellationToken.None));

        Assert.Equal(FileEntryKind.File, entry.Kind);
        Assert.Equal(1234, entry.Size);
        Assert.Equal(modified, entry.Modified.UtcTicks);
        Assert.True(entry.IsHidden);
    }

    [Fact]
    public async Task ReadersAndAWriter_DoNotCorruptTheIndex()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(P(@"C:\"));
        var writer = Task.Run(() =>
        {
            for (var round = 0; round < 200; round++)
            {
                index.ReplaceChildren(root, [.. Enumerable.Range(0, 50).Select(i => File($"r{round % 3}_{i}.txt"))]);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                foreach (var entry in index.Candidates("r1_", [], null, int.MaxValue, CancellationToken.None))
                {
                    Assert.StartsWith(@"C:\r", entry.Location.ToString(), StringComparison.Ordinal);
                }
            }
        })).ToArray();

        await Task.WhenAll([writer, .. readers]);

        Assert.Equal(50, index.Count);
    }

    // ---- the crawler ----

    private static FakeLocationProvider Tree() => new FakeLocationProvider()
        .AddFile(@"C:\root\a.mkv", 10)
        .AddFile(@"C:\root\sub\b.mkv", 20)
        .AddFile(@"C:\root\sub\deeper\c.txt", 30)
        .AddFolder(@"C:\root\empty")
        .AddLink(@"C:\root\loop");

    [Fact]
    public async Task Crawl_ListsTheWholeTree_AndSkipsNothingButLinksAsFolders()
    {
        using var index = new NameIndex();
        var crawler = new IndexCrawler(new LocationProviderRegistry([Tree()]), index);

        var progress = await crawler.CrawlAsync(P(@"C:\root"), null, CancellationToken.None);

        Assert.Equal(4, progress.FoldersDone); // root, sub, deeper, empty (loop is a link and is not entered)
        Assert.Equal(0, progress.Errors);
        Assert.Equal(P(@"C:\root\sub\deeper\c.txt"), index.PathOf(index.Find(P(@"C:\root\sub\deeper\c.txt"))));
        Assert.Equal(7, index.Count);
    }

    [Fact]
    public async Task Crawl_Again_AppliesOnlyTheDifferences_AndKeepsIds()
    {
        var files = Tree();
        using var index = new NameIndex();
        var crawler = new IndexCrawler(new LocationProviderRegistry([files]), index);
        await crawler.CrawlAsync(P(@"C:\root"), null, CancellationToken.None);
        var before = index.Find(P(@"C:\root\sub\b.mkv"));

        files.AddFile(@"C:\root\sub\new.mkv", 1);
        await crawler.CrawlAsync(P(@"C:\root"), null, CancellationToken.None);

        Assert.Equal(before, index.Find(P(@"C:\root\sub\b.mkv")));
        Assert.True(index.Find(P(@"C:\root\sub\new.mkv")) >= 0);
        Assert.Equal(8, index.Count);
    }

    private sealed class FailingFolders(FakeLocationProvider inner, params string[] failing) : ILocationProvider
    {
        public string Name => "failing";

        public bool CanHandle(LocationPath location) => true;

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (failing.Contains(location.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                await Task.Yield();
                throw new LocationException(LocationError.AccessDenied, location, "Access is denied.");
            }

            await foreach (var batch in inner.ListAsync(location, options, cancellationToken))
            {
                yield return batch;
            }
        }
    }

    [Fact]
    public async Task Crawl_AFolderThatFails_KeepsWhatTheIndexHadForIt()
    {
        var files = Tree();
        using var index = new NameIndex();
        await new IndexCrawler(new LocationProviderRegistry([files]), index).CrawlAsync(P(@"C:\root"), null, CancellationToken.None);

        var progress = await new IndexCrawler(new LocationProviderRegistry([new FailingFolders(files, @"C:\root\sub")]), index).CrawlAsync(P(@"C:\root"), null, CancellationToken.None);

        Assert.Equal(1, progress.Errors);
        Assert.True(index.Find(P(@"C:\root\sub\b.mkv")) >= 0);
    }

    private sealed class InlineProgress(Action<CrawlProgress> report) : IProgress<CrawlProgress>
    {
        public void Report(CrawlProgress value) => report(value);
    }

    [Fact]
    public async Task Crawl_Cancelled_Throws_AndLeavesAUsableIndex()
    {
        var files = new FakeLocationProvider();
        for (var i = 0; i < 100; i++)
        {
            files.AddFile($@"C:\big\f{i}\x.txt", 1);
        }

        using var index = new NameIndex();
        using var cts = new CancellationTokenSource();
        // Progress<T> would call back later on the pool, possibly after the test (and its token source) is gone, and crash the test host.
        var reports = new InlineProgress(p =>
        {
            if (p.FoldersDone >= 5)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IndexCrawler(new LocationProviderRegistry([files]), index).CrawlAsync(P(@"C:\big"), reports, cts.Token));

        Assert.True(index.Count >= 1);
        Assert.True(index.Count < 200);
        _ = Find(index, "x.txt"); // still searchable
    }

    // ---- searching the index agrees with the live search ----

    private static async Task<List<string>> ViaIndex(NameIndex index, string text, params LocationPath[] roots)
    {
        var query = QueryParser.Parse(text);
        var matcher = new QueryMatcher(query, new ManualTimeProvider(), TimeZoneInfo.Utc);
        var found = new List<string>();
        await foreach (var batch in new NameIndexSearch(index).RunAsync(query, roots, matcher, NoSearchFacts.Instance, CancellationToken.None))
        {
            found.AddRange(batch.Matches.Select(m => m.Location.ToString()));
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> ViaLive(FakeLocationProvider files, string text, params LocationPath[] roots)
    {
        var query = QueryParser.Parse(text);
        var matcher = new QueryMatcher(query, new ManualTimeProvider(), TimeZoneInfo.Utc);
        var found = new List<string>();
        await foreach (var batch in new LiveSearch(new LocationProviderRegistry([files])).RunAsync(roots, matcher, NoSearchFacts.Instance, CancellationToken.None))
        {
            found.AddRange(batch.Matches.Select(m => m.Location.ToString()));
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Search_GivesTheSameAnswersAsTheLiveSearch_ForManyQueries()
    {
        var random = new Random(11);
        var files = new FakeLocationProvider();
        string[] words = ["holiday", "Holiday", "report", "IMG", "draft", "final", "2025", "clip", "notes", "x"];
        string[] extensions = ["mkv", "mp4", "txt", "jpg", "pdf", "zip", "cs"];
        string[] folders = ["", "a", "a\\b", "c", "c\\d\\e", "Videos", "Videos\\2025"];
        for (var i = 0; i < 400; i++)
        {
            var folder = folders[random.Next(folders.Length)];
            var name = $"{words[random.Next(words.Length)]}_{words[random.Next(words.Length)]}{i}.{extensions[random.Next(extensions.Length)]}";
            files.AddFile(@"C:\tree\" + (folder.Length == 0 ? string.Empty : folder + "\\") + name, random.Next(0, 5000));
        }

        using var index = new NameIndex();
        await new IndexCrawler(new LocationProviderRegistry([files]), index).CrawlAsync(P(@"C:\tree"), null, CancellationToken.None);
        string[] queries =
        [
            "holiday", "HOLIDAY final", "report -draft", "IMG_*", "*.mkv", "ext:mp4,jpg", "size:>1KB", "type:video", "type:folder", "type:image 2025",
            "clip size:<100", "\"holiday_report\"", "regex:^x_", "x", "path:Videos", "ext:cs -notes", "?mg*", "zzz", "holiday_*7.*", "type:file report",
        ];

        foreach (var text in queries)
        {
            Assert.Equal(await ViaLive(files, text, P(@"C:\tree")), await ViaIndex(index, text, P(@"C:\tree")));
            Assert.Equal(await ViaLive(files, text, P(@"C:\tree\a")), await ViaIndex(index, text, P(@"C:\tree\a")));
        }
    }

    [Fact]
    public void LongestLiteral_PicksTheLongestPlainRun_AndIgnoresExcludedWords()
    {
        Assert.Equal("holiday", NameIndexSearch.LongestLiteral(QueryParser.Parse("holiday x")));
        Assert.Equal("report", NameIndexSearch.LongestLiteral(QueryParser.Parse("IMG_*report?.jpg")));
        Assert.Equal("report", NameIndexSearch.LongestLiteral(QueryParser.Parse("*report*")));
        Assert.Null(NameIndexSearch.LongestLiteral(QueryParser.Parse("-draft ext:mkv")));
        Assert.Null(NameIndexSearch.LongestLiteral(QueryParser.Parse("*")));
    }
}
