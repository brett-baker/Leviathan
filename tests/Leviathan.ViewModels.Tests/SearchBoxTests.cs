using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Search;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Searches;
using Leviathan.ViewModels.Tags;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>The search box: what a typed search means, the words in the status bar, and the facts a search reads (spec §13).</summary>
public sealed class SearchBoxTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void Plan_ValidQuery_SearchesTheCurrentFolderAndBelow()
    {
        var plan = SearchStarter.Plan("  holiday type:video  ", P(@"C:\Videos"), includeHidden: false);

        Assert.Null(plan.Message);
        var request = SearchLocations.TryRead(plan.Results!);
        Assert.Equal("holiday type:video", request!.QueryText);
        Assert.Equal([P(@"C:\Videos")], request.Roots);
        Assert.False(request.IncludeHidden);
    }

    [Fact]
    public void Plan_FollowsTheHiddenItemsSetting()
    {
        var request = SearchLocations.TryRead(SearchStarter.Plan("a", P(@"C:\x"), includeHidden: true).Results!);

        Assert.True(request!.IncludeHidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_NothingTyped_SaysWhatToType(string? text)
    {
        var plan = SearchStarter.Plan(text, P(@"C:\x"), false);

        Assert.Null(plan.Results);
        Assert.Contains("Type something", plan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_BadQuery_QuotesTheOffendingPart_AndStartsNothing()
    {
        var plan = SearchStarter.Plan("holiday size:5XB", P(@"C:\x"), false);

        Assert.Null(plan.Results);
        Assert.Contains("size:5XB", plan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_Content_IsRefusedWithAnExplanation_RatherThanFindingNothing()
    {
        var plan = SearchStarter.Plan("content:invoice", P(@"C:\x"), false);

        Assert.Null(plan.Results);
        Assert.Contains("not available yet", plan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_FromAShellPage_AsksForAFolder()
    {
        var plan = SearchStarter.Plan("a", P(@"shell:tag\3"), false);

        Assert.Null(plan.Results);
        Assert.Contains("Open a folder", plan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_FromAResultsTab_RefinesTheSameFolders()
    {
        var first = SearchStarter.Plan("holiday", P(@"C:\Videos"), false).Results!;

        var refined = SearchStarter.Plan("holiday type:video", first, false);

        var request = SearchLocations.TryRead(refined.Results!);
        Assert.Equal("holiday type:video", request!.QueryText);
        Assert.Equal([P(@"C:\Videos")], request.Roots);
    }

    [Fact]
    public async Task SearchRunCommand_OpensTheResultsInTheSameTab_AndBackReturns()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        var result = await h.Run(CommandIds.SearchRun, "holiday");

        Assert.Equal(CommandResult.Executed, result);
        Assert.True(SearchLocations.IsSearchLocation(h.Tab.History.Current));
        Assert.Equal("Search: holiday", h.Tab.Title);
        await h.Run(CommandIds.NavBack);
        Assert.Equal(P(@"C:\a"), h.Tab.History.Current);
    }

    [Fact]
    public async Task SearchRunCommand_WithABadQuery_ShowsTheMessage_AndStaysPut()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.SearchRun, "size:nope");

        Assert.Equal(P(@"C:\a"), h.Tab.History.Current);
        Assert.Single(h.Shell.Messages);
    }

    [Fact]
    public async Task SearchFocusCommand_FocusesTheBox_AndHasCtrlF()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.SearchFocus);

        Assert.Equal(1, h.Shell.SearchFocusCalls);
        Assert.True(h.Commands.TryGet(CommandIds.SearchFocus, out var command));
        Assert.Equal("Ctrl+F", command.Definition.DefaultShortcut);
    }

    [Fact]
    public void AddressBar_ShowsOneCrumbThatSaysWhatWasSearched()
    {
        var bar = new AddressBarViewModel(SearchStarter.Plan("holiday", P(@"C:\Videos"), false).Results!);

        var crumb = Assert.Single(bar.Breadcrumbs);
        Assert.Equal("Search for \"holiday\" in C:\\Videos", crumb.Label);
        bar.BeginEdit();
        Assert.Equal(crumb.Label, bar.EditText);
    }

    private static SearchReport Report(int matches, int folders, int errors, bool cancelled = false, bool finished = false, string query = "holiday") =>
        new(new SearchRequest(query, []), new SearchProgress(folders, folders * 10L, matches, errors), [], cancelled, finished);

    [Fact]
    public void Status_WhileRunning_CountsWhatIsFoundSoFar() =>
        Assert.Equal("Searching… 12 matches so far, 340 folders searched", SearchStatusText.For(Report(12, 340, 0)));

    [Fact]
    public void Status_WhenDone_SaysHowMuchWasSearched() =>
        Assert.Equal("1 match in 1 folder searched", SearchStatusText.For(Report(1, 1, 0, finished: true)));

    [Fact]
    public void Status_WhenStopped_SaysNotEverythingWasSearched() =>
        Assert.Contains("not everything was searched", SearchStatusText.For(Report(3, 20, 0, cancelled: true)), StringComparison.Ordinal);

    [Fact]
    public void Status_NeverHidesFolderErrors() =>
        Assert.EndsWith("2 folders could not be read", SearchStatusText.For(Report(3, 20, 2, finished: true)), StringComparison.Ordinal);

    [Fact]
    public void Status_ForMediaFields_SaysTheyOnlyKnowWhatHasBeenLookedAt() =>
        Assert.Contains("media information is already known", SearchStatusText.For(Report(0, 5, 0, finished: true, query: "duration:>1h")), StringComparison.Ordinal);

    [Fact]
    public void Status_SaysWhereTheAnswersCameFrom_WhenItWasNotAPlainLiveListing()
    {
        var report = Report(2, 5, 0, finished: true) with
        {
            Sources =
            [
                new SearchSourceChoice(SearchSourceKind.Everything, [P(@"C:\v")]),
                new SearchSourceChoice(SearchSourceKind.Index, [P(@"\\nas\media")], new DateTimeOffset(2026, 11, 10, 8, 30, 0, TimeSpan.Zero)),
                new SearchSourceChoice(SearchSourceKind.Live, [P(@"D:\x")], null, "Everything is installed but not running"),
            ],
        };

        var text = SearchStatusText.For(report);

        Assert.Contains("from Everything", text, StringComparison.Ordinal);
        Assert.Contains("from the index, last listed 2026-11-10", text, StringComparison.Ordinal);
        Assert.Contains("folders listed now (everything is installed but not running)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("from Everything", SearchStatusText.For(Report(1, 1, 0, finished: true) with { Sources = [new SearchSourceChoice(SearchSourceKind.Live, [])] }), StringComparison.Ordinal);
    }

    private sealed class Tags(Dictionary<string, HashSet<int>> byPath) : ITagStore
    {
        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<int>>(byPath.TryGetValue(path, out var ids) ? ids : []);

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static FileEntry File(string path, long size = 100)
    {
        var location = P(path);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new FileEntry(location.Name, location, FileEntryKind.File, size, now, now, FileAttributes.Normal);
    }

    [Fact]
    public async Task Facts_Tags_AreNamesFromTheCatalog_NotIds()
    {
        var facts = new AppSearchFacts(new Tags(new() { [@"C:\a.mkv"] = [1, 3] }), () => TagCatalog.Default, media: null);

        var names = await facts.GetTagsAsync(File(@"C:\a.mkv"), CancellationToken.None);

        Assert.Equal(TagCatalog.Default.Tags.Where(t => t.Id is 1 or 3).Select(t => t.Name), names);
        Assert.Empty(await facts.GetTagsAsync(File(@"C:\b.mkv"), CancellationToken.None));
    }

    private sealed class CachedMedia(Dictionary<string, MediaInfoResult> known) : IMediaInfoSource
    {
        public int Probes { get; private set; }

        public Task<IReadOnlyDictionary<string, MediaInfoResult>> PeekAsync(IReadOnlyList<MediaFileRef> files, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, MediaInfoResult>>(files.Where(f => known.ContainsKey(f.Path)).ToDictionary(f => f.Path, f => known[f.Path]));

        public Task<MediaInfoResult> ProbeAsync(MediaFileRef file, CancellationToken cancellationToken)
        {
            Probes++;
            throw new InvalidOperationException("A search must not probe.");
        }
    }

    [Fact]
    public async Task Facts_Media_ComesFromTheCacheOnly()
    {
        var info = new Leviathan.Media.MediaInfo(
            "matroska", "matroska,webm", TimeSpan.FromMinutes(90), null, null,
            [new Leviathan.Media.VideoStreamInfo(0, "HEVC", null, 3840, 2160, 24, null, null, null, Leviathan.Media.HdrKind.None, false, null, null)],
            [new Leviathan.Media.AudioStreamInfo(1, "AAC", 2, null, null, null, null, null, true)],
            [], [], new Dictionary<string, string>());
        var source = new CachedMedia(new() { [@"C:\a.mkv"] = new MediaInfoResult(MediaInfoState.Ok, info, null) });
        var facts = new AppSearchFacts(new Tags([]), () => TagCatalog.Default, source);

        var known = await facts.GetMediaAsync(File(@"C:\a.mkv"), CancellationToken.None);
        var unknown = await facts.GetMediaAsync(File(@"C:\b.mkv"), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(90), known!.Duration);
        Assert.Equal(2160, known.Height);
        Assert.Equal(["hevc", "aac"], known.Codecs);
        Assert.Null(unknown);
        Assert.Equal(0, source.Probes);
    }
}
