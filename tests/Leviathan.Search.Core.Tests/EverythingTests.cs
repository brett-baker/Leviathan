using Leviathan.Core.Paths;
using Leviathan.Testing;

namespace Leviathan.Search.Core.Tests;

/// <summary>Everything as a source of results (spec §13): what is asked of it, and that its answers are judged by the same matcher.</summary>
public sealed class EverythingTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static string Translate(string query, params string[] roots) =>
        EverythingQueryTranslator.Translate(QueryParser.Parse(query), [.. roots.Select(P)]).Search;

    [Theory]
    [InlineData("holiday", @"C:\Videos", "\"C:\\Videos\\\" holiday")]
    [InlineData("holiday 2025", @"C:\Videos", "\"C:\\Videos\\\" holiday 2025")]
    [InlineData("\"summer holiday\"", @"C:\Videos", "\"C:\\Videos\\\" \"summer holiday\"")]
    [InlineData("IMG_*.jpg", @"D:\p", "\"D:\\p\\\" IMG_*.jpg")]
    [InlineData("ext:mkv,mp4", @"C:\", "\"C:\\\" ext:mkv;mp4")]
    [InlineData("size:>2GB", @"C:\a", "\"C:\\a\\\" size:>2147483648")]
    [InlineData("size:1MB..2MB", @"C:\a", "\"C:\\a\\\" size:1048576..2097152")]
    [InlineData("type:folder", @"C:\a", "\"C:\\a\\\" folder:")]
    [InlineData("type:file", @"C:\a", "\"C:\\a\\\" file:")]
    public void Translate_PushesWhatEverythingJudgesAtLeastAsLooselyAsWeDo(string query, string root, string expected) =>
        Assert.Equal(expected, Translate(query, root));

    [Fact]
    public void Translate_TypeVideo_PushesTheVideoExtensions()
    {
        var text = Translate("type:video", @"C:\a");

        Assert.StartsWith("\"C:\\a\\\" ext:", text, StringComparison.Ordinal);
        Assert.Contains("mkv", text, StringComparison.Ordinal);
        Assert.DoesNotContain("mp3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_SeveralFolders_AreAGroupOfAlternatives() =>
        Assert.Equal("<\"C:\\a\\\"|\"D:\\b\\\"> holiday", Translate("holiday", @"C:\a", @"D:\b"));

    [Theory]
    [InlineData("-draft")]
    [InlineData("tag:red")]
    [InlineData("modified:lastweek")]
    [InlineData("duration:>1h")]
    [InlineData("regex:^a")]
    [InlineData("path:inbox")]
    [InlineData("a!b")]
    public void Translate_LeavesOutWhatItCannotSayExactly_SoNothingIsLost(string query)
    {
        var plan = EverythingQueryTranslator.Translate(QueryParser.Parse(query), [P(@"C:\a")]);

        Assert.Equal("\"C:\\a\\\"", plan.Search);
        Assert.False(plan.Narrows);
    }

    [Fact]
    public void Translate_KeepsTheNarrowingPart_WhenMixedWithWhatCannotBePushed()
    {
        var plan = EverythingQueryTranslator.Translate(QueryParser.Parse("holiday -draft tag:red ext:mp4"), [P(@"C:\a")]);

        Assert.Equal("\"C:\\a\\\" holiday ext:mp4", plan.Search);
        Assert.True(plan.Narrows);
    }

    [Fact]
    public void CanAnswer_NeedsANarrowingTerm_AndLocalFolders()
    {
        Assert.True(EverythingSearch.CanAnswer(QueryParser.Parse("holiday"), [P(@"C:\a")]));
        Assert.False(EverythingSearch.CanAnswer(QueryParser.Parse("tag:red"), [P(@"C:\a")]));
        Assert.False(EverythingSearch.CanAnswer(QueryParser.Parse("holiday"), [P(@"\\nas\media")]));
        Assert.False(EverythingSearch.CanAnswer(QueryParser.Parse("holiday"), [P(@"C:\a"), P(@"\\nas\media")]));
        Assert.False(EverythingSearch.CanAnswer(QueryParser.Parse("holiday"), []));
    }

    private static EverythingHit Hit(string path, long size = 10, bool folder = false, FileAttributes attributes = FileAttributes.Normal)
    {
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        return new EverythingHit(path, size, now, now, folder ? FileAttributes.Directory : attributes, folder);
    }

    private static async Task<(List<string> Names, SearchBatch Last)> RunAsync(FakeEverythingClient client, string text, string root, EverythingSearchOptions? options = null)
    {
        var query = QueryParser.Parse(text);
        var matcher = new QueryMatcher(query, new ManualTimeProvider(), TimeZoneInfo.Utc);
        var names = new List<string>();
        SearchBatch? last = null;
        await foreach (var batch in new EverythingSearch(client, options).RunAsync(query, [P(root)], matcher, NoSearchFacts.Instance, CancellationToken.None))
        {
            names.AddRange(batch.Matches.Select(m => m.Location.ToString()));
            last = batch;
        }

        return (names, last!);
    }

    [Fact]
    public async Task Run_AsksWithTheTranslatedText_AndReturnsEntries()
    {
        var client = new FakeEverythingClient();
        client.Items.AddRange([Hit(@"C:\v\holiday.mp4", 5), Hit(@"C:\v\sub", folder: true)]);
        client.Matches = (_, hit) => hit.FullPath.Contains("holiday", StringComparison.Ordinal);

        var (names, last) = await RunAsync(client, "holiday", @"C:\v");

        Assert.Equal([@"C:\v\holiday.mp4"], names);
        Assert.Equal("\"C:\\v\\\" holiday", Assert.Single(client.Searches));
        Assert.True(last.IsFinal);
        Assert.Equal(1, last.Progress.Matches);
    }

    [Fact]
    public async Task Run_JudgesEveryAnswerWithTheMatcher_NotJustTheOnesEverythingPushed()
    {
        // Everything returns both, as it would for a query it only partly understood: the excluded term must still exclude.
        var client = new FakeEverythingClient();
        client.Items.AddRange([Hit(@"C:\v\holiday final.mp4"), Hit(@"C:\v\holiday draft.mp4")]);

        var (names, _) = await RunAsync(client, "holiday -draft", @"C:\v");

        Assert.Equal([@"C:\v\holiday final.mp4"], names);
    }

    [Fact]
    public async Task Run_DropsItemsOutsideTheFolders_AndTheFolderItself()
    {
        var client = new FakeEverythingClient();
        client.Items.AddRange([Hit(@"C:\v\a.txt"), Hit(@"C:\other\a.txt"), Hit(@"C:\v", folder: true), Hit(@"C:\vv\a.txt")]);

        var (names, _) = await RunAsync(client, "a", @"C:\v");

        Assert.Equal([@"C:\v\a.txt"], names);
    }

    [Fact]
    public async Task Run_SkipsHiddenItemsUnlessAsked()
    {
        var client = new FakeEverythingClient();
        client.Items.AddRange([Hit(@"C:\v\a.txt"), Hit(@"C:\v\b.txt", attributes: FileAttributes.Hidden)]);

        Assert.Equal([@"C:\v\a.txt"], (await RunAsync(client, "txt", @"C:\v")).Names);
        Assert.Equal(2, (await RunAsync(client, "txt", @"C:\v", new EverythingSearchOptions { IncludeHidden = true })).Names.Count);
    }

    [Fact]
    public async Task Run_ReadsPageByPage_UntilTheEnd_AndStopsAtTheCap()
    {
        var client = new FakeEverythingClient();
        client.Items.AddRange(Enumerable.Range(0, 25).Select(i => Hit($@"C:\v\f{i:D2}.txt")));

        var (names, _) = await RunAsync(client, "txt", @"C:\v", new EverythingSearchOptions { PageSize = 10 });
        Assert.Equal(25, names.Count);
        Assert.Equal([(0, 10), (10, 10), (20, 10)], client.Pages);

        var capped = new FakeEverythingClient();
        capped.Items.AddRange(client.Items);
        var (some, _) = await RunAsync(capped, "txt", @"C:\v", new EverythingSearchOptions { PageSize = 10, MaxHits = 15 });
        Assert.Equal(20, some.Count); // whole pages: 15 allowed, the page that crosses the cap is read to its end
    }

    [Fact]
    public async Task Run_Cancelled_Throws()
    {
        var client = new FakeEverythingClient();
        client.Items.Add(Hit(@"C:\v\a.txt"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var query = QueryParser.Parse("a");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new EverythingSearch(client).RunAsync(query, [P(@"C:\v")], new QueryMatcher(query), NoSearchFacts.Instance, cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Run_NotReady_Throws_SoTheCallerPicksAnotherSource()
    {
        var client = new FakeEverythingClient { State = EverythingState.NotRunning };
        var query = QueryParser.Parse("a");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new EverythingSearch(client).RunAsync(query, [P(@"C:\v")], new QueryMatcher(query), NoSearchFacts.Instance, CancellationToken.None))
            {
            }
        });
    }
}
