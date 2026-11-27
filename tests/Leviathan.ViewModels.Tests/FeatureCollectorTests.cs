using System.Net;
using Leviathan.Testing;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.ViewModels.Tests;

/// <summary>What the learning gathers about each decided file, in the background: the name, the video, and what each server says.</summary>
public sealed class FeatureCollectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Path1 = @"\\nas\Private\Renamed\Working Folder\Primal's FOOTJOBS.Dr. Cadence Lux - Pantyhose.Cadence Lux.1080p.FHD.mp4";

    // ---- fakes ----

    private sealed class Store : IFileFactStore
    {
        public Dictionary<string, FileFacts> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<FileFacts?> GetAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(path));

        public Task SaveAsync(FileFacts facts, CancellationToken cancellationToken)
        {
            lock (Items)
            {
                Items[facts.Path] = facts;
            }

            return Task.CompletedTask;
        }

        public Task<(int Complete, int Incomplete)> CountAsync(CancellationToken cancellationToken) =>
            Task.FromResult((Items.Values.Count(f => f.Complete), Items.Values.Count(f => !f.Complete)));

        public Task<IReadOnlyList<FileFacts>> LoadAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FileFacts>>([.. Items.Values]);
    }

    private sealed class Video : IVideoFactsSource
    {
        public VideoFacts? Answer { get; set; } = new(900, 1920, 1080, "h264", 5_000_000);

        public Exception? Fail { get; set; }

        public int Calls { get; private set; }

        public Task<VideoFacts?> GetAsync(string path, long size, long modifiedTicks, CancellationToken cancellationToken)
        {
            Calls++;
            return Fail is { } f ? throw f : Task.FromResult(Answer);
        }
    }

    private sealed class Scenes : ISceneLookup
    {
        public Dictionary<string, SceneLookupResult> Answers { get; } = [];

        public List<string> Asked { get; } = [];

        public Task<SceneLookupResult> FindAsync(LearningSource source, string? secret, string path, long size, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(source.Id + ":" + secret);
            }

            return Task.FromResult(Answers.GetValueOrDefault(source.Id) ?? new SceneLookupResult(true, null));
        }
    }

    private sealed class Secrets : ILearningSecrets
    {
        public string? Get(string sourceId) => "key-" + sourceId;

        public void Set(string sourceId, string? secret) => throw new NotSupportedException();
    }

    private sealed class Rig
    {
        public Store Facts { get; } = new();

        public Video VideoSource { get; } = new();

        public Scenes SceneSource { get; } = new();

        public FakeTriageHistory History { get; } = new();

        public LearningSettings Settings { get; set; } = new() { Enabled = true };

        public FeatureCollector Collector { get; }

        public Rig() => Collector = new FeatureCollector(Facts, VideoSource, SceneSource, () => Settings, new Secrets(), History, new ManualTimeProvider(T0));
    }

    private static TriageDecisionRecord Decided(string path, TriageDecision decision = TriageDecision.Kept, int minute = 0) =>
        new(T0.AddMinutes(minute), path, decision, null, 1000, 2000);

    private static LearningSource Server(string id) => new(id, LearningSourceKind.Stash, "Home", "http://nas:9999");

    // ---- collecting ----

    [Fact]
    public async Task ADecidedFile_GetsItsNameAndVideoFacts_WithNoServer()
    {
        var rig = new Rig();

        rig.Collector.Enqueue(Decided(Path1));
        await rig.Collector.Idle;

        var facts = rig.Facts.Items[Path1];
        Assert.True(facts.Complete);
        Assert.Equal("Working Folder", facts.Name.Folder);
        Assert.Contains("cadence", facts.Name.Tokens);
        Assert.Equal(1080, facts.Video!.Height);
        Assert.Empty(facts.Scenes);
        Assert.Equal(0, rig.Collector.Pending);
    }

    [Fact]
    public async Task WhileLearningIsOff_NothingIsCollected()
    {
        var rig = new Rig { Settings = new LearningSettings { Enabled = false } };

        rig.Collector.Enqueue(Decided(Path1));
        await rig.Collector.Idle;

        Assert.Empty(rig.Facts.Items);
        Assert.Equal(0, rig.VideoSource.Calls);
    }

    [Theory]
    [InlineData(TriageDecision.Skipped)]
    [InlineData(TriageDecision.None)]
    public async Task ASkipOrATakenBackDecision_IsNotCollected(TriageDecision decision)
    {
        var rig = new Rig();

        rig.Collector.Enqueue(Decided(Path1, decision));
        await rig.Collector.Idle;

        Assert.Empty(rig.Facts.Items);
    }

    [Fact]
    public async Task AVideoThatCannotBeRead_StillLeavesTheNameFacts()
    {
        var rig = new Rig();
        rig.VideoSource.Fail = new IOException("file gone");

        rig.Collector.Enqueue(Decided(Path1));
        await rig.Collector.Idle;

        var facts = rig.Facts.Items[Path1];
        Assert.Null(facts.Video);
        Assert.True(facts.Complete);
    }

    [Fact]
    public async Task AServer_AddsTheSceneItKnows_AndIsAskedWithItsOwnKey()
    {
        var rig = new Rig { Settings = new LearningSettings { Enabled = true, Sources = [Server("s1")] } };
        var scene = new SceneFacts("s1", "Pantyhose", "Primal Fetish", "2016-05-01", 80, ["Cadence Lux"], ["feet", "glasses"]);
        rig.SceneSource.Answers["s1"] = new SceneLookupResult(true, scene);

        rig.Collector.Enqueue(Decided(Path1));
        await rig.Collector.Idle;

        var facts = rig.Facts.Items[Path1];
        Assert.Equal([scene], facts.Scenes);
        Assert.Equal(["s1"], facts.Asked);
        Assert.True(facts.Complete);
        Assert.Equal(["s1:key-s1"], rig.SceneSource.Asked);
    }

    [Fact]
    public async Task AServerThatKnowsNothingOfTheFile_StillCountsAsAsked()
    {
        var rig = new Rig { Settings = new LearningSettings { Enabled = true, Sources = [Server("s1")] } };

        rig.Collector.Enqueue(Decided(Path1));
        await rig.Collector.Idle;

        Assert.True(rig.Facts.Items[Path1].Complete);
        Assert.Empty(rig.Facts.Items[Path1].Scenes);
    }

    [Fact]
    public async Task AServerThatCannotBeReached_LeavesTheFileIncomplete_UntilABackfill()
    {
        var rig = new Rig { Settings = new LearningSettings { Enabled = true, Sources = [Server("s1")] } };
        await rig.History.AppendAsync(Decided(Path1), CancellationToken.None);
        rig.SceneSource.Answers["s1"] = new SceneLookupResult(false, null);
        rig.Collector.Enqueue(Decided(Path1));
        await rig.Collector.Idle;
        Assert.False(rig.Facts.Items[Path1].Complete);

        var scene = new SceneFacts("s1", "T", null, null, null, [], []);
        rig.SceneSource.Answers["s1"] = new SceneLookupResult(true, scene);
        var queued = await rig.Collector.BackfillAsync(CancellationToken.None);
        await rig.Collector.Idle;

        Assert.Equal(1, queued);
        Assert.True(rig.Facts.Items[Path1].Complete);
        Assert.Equal([scene], rig.Facts.Items[Path1].Scenes);
    }

    [Fact]
    public async Task ABackfill_AsksANewServerAboutFilesCollectedBeforeItExisted_WithoutProbingTheVideoAgain()
    {
        var rig = new Rig();
        await rig.History.AppendAsync(Decided(Path1), CancellationToken.None);
        rig.Collector.Enqueue(Decided(Path1));
        await rig.Collector.Idle;
        Assert.Equal(1, rig.VideoSource.Calls);

        rig.Settings = new LearningSettings { Enabled = true, Sources = [Server("s2")] };
        var queued = await rig.Collector.BackfillAsync(CancellationToken.None);
        await rig.Collector.Idle;

        Assert.Equal(1, queued);
        Assert.Equal(["s2"], rig.Facts.Items[Path1].Asked);
        Assert.Equal(1, rig.VideoSource.Calls);
    }

    [Fact]
    public async Task ABackfill_LeavesCompleteFilesAlone_AndUsesTheLatestDecisionOnly()
    {
        var rig = new Rig();
        await rig.History.AppendAsync(Decided(@"Z:\a.mp4", TriageDecision.Discarded, 0), CancellationToken.None);
        await rig.History.AppendAsync(Decided(@"Z:\a.mp4", TriageDecision.None, 1), CancellationToken.None);
        await rig.History.AppendAsync(Decided(@"Z:\b.mp4", TriageDecision.Kept, 2), CancellationToken.None);

        Assert.Equal(1, await rig.Collector.BackfillAsync(CancellationToken.None));
        await rig.Collector.Idle;
        Assert.Equal(0, await rig.Collector.BackfillAsync(CancellationToken.None));
        Assert.Equal([@"Z:\b.mp4"], rig.Facts.Items.Keys);
    }

    [Fact]
    public async Task ABackfill_WhileLearningIsOff_QueuesNothing()
    {
        var rig = new Rig { Settings = new LearningSettings { Enabled = false } };
        await rig.History.AppendAsync(Decided(Path1), CancellationToken.None);

        Assert.Equal(0, await rig.Collector.BackfillAsync(CancellationToken.None));
    }

    // ---- the recorder hands decisions over ----

    private sealed class NoActions : ITriageActions
    {
        public Task<TriageActionResult> KeepAsync(Leviathan.Core.Model.FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<TriageActionResult> DiscardAsync(Leviathan.Core.Model.FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<bool> ToggleTagAsync(Leviathan.Core.Model.FileEntry file, int tagNumber, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ClearTagsAsync(Leviathan.Core.Model.FileEntry file, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static Leviathan.Core.Model.FileEntry Entry(string path)
    {
        var location = Leviathan.Core.Paths.LocationPath.Parse(path);
        return new Leviathan.Core.Model.FileEntry(location.Name, location, Leviathan.Core.Model.FileEntryKind.File, 4321, T0, T0, FileAttributes.Normal);
    }

    [Fact]
    public async Task TheRecorder_HandsEachDecisionToTheCollector_AfterItIsInTheHistory()
    {
        var handed = new List<TriageDecisionRecord>();
        var history = new FakeTriageHistory();
        var seenInHistory = new List<bool>();
        var session = new TriageSession([Entry(@"Z:\a.mp4"), Entry(@"Z:\b.mp4")], new FakeVideoEngine(), new NoActions(), new TriageOptions { StartOffset = 0 });
        using var recorder = new TriageRecorder(
            session,
            new TriageJournal(new System.IO.Abstractions.TestingHelpers.MockFileSystem(), @"C:\data\j.jsonl"),
            string.Empty,
            () => null,
            history,
            new ManualTimeProvider(T0),
            decided: r =>
            {
                lock (handed)
                {
                    handed.Add(r);
                    seenInHistory.Add(history.Records.Contains(r));
                }
            });
        await session.StartAsync();

        await session.DiscardAsync("short");
        await session.KeepAsync();
        await recorder.Flushed;

        Assert.Equal([TriageDecision.Discarded, TriageDecision.Kept], handed.Select(r => r.Decision));
        Assert.Equal("short", handed[0].ReasonId);
        Assert.Equal(4321, handed[0].Size);
        Assert.All(seenInHistory, Assert.True);
    }
    // ---- the name ----

    [Fact]
    public void NameParser_FindsWordsFolderYearAndResolution()
    {
        var facts = NameParser.Parse(@"\\nas\Private\Renamed\Working Folder\Studio.Some Title - Jane Doe 2019.1080p.FHD.MP4");

        Assert.Equal("Working Folder", facts.Folder);
        Assert.Equal(".mp4", facts.Extension);
        Assert.Equal(2019, facts.Year);
        Assert.Equal(1080, facts.Resolution);
        Assert.Contains("jane", facts.Tokens);
        Assert.Contains("title", facts.Tokens);
        Assert.DoesNotContain("mp4", facts.Tokens);
    }

    [Theory]
    [InlineData(@"C:\a\b.mp4", "a", ".mp4")]
    [InlineData(@"C:\noext", "C:", "")]
    [InlineData("loose.mkv", "", ".mkv")]
    public void NameParser_HandlesPlacesWithAndWithoutFolders(string path, string folder, string extension)
    {
        var facts = NameParser.Parse(path);

        Assert.Equal(folder, facts.Folder);
        Assert.Equal(extension, facts.Extension);
    }

    [Fact]
    public void NameParser_KeepsEachWordOnce_AndCapsTheList()
    {
        var facts = NameParser.Parse(@"C:\x\" + string.Join(' ', Enumerable.Range(0, 100).Select(i => "word" + i)) + " word1 word1.mp4");

        Assert.Equal(40, facts.Tokens.Count);
        Assert.Equal(facts.Tokens.Count, facts.Tokens.Distinct().Count());
    }

    // ---- the Stash lookup ----

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is { } c ? await c.ReadAsStringAsync(cancellationToken) : null;
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static Task<SceneLookupResult> Find(Handler handler, string path = @"\\nas\x\My Scene.mp4", long size = 1000) =>
        new StashSceneLookup(() => handler).FindAsync(Server("s1"), "k", path, size, CancellationToken.None);

    private const string TwoScenes = """
        {"data":{"findScenes":{"scenes":[
          {"title":"Other","date":"2020-01-01","rating100":null,"studio":null,"performers":[],"tags":[],"files":[{"path":"/data/porn/x/My Scene Extra.mp4","size":1000}]},
          {"title":"Mine","date":"2019-02-03","rating100":70,"studio":{"name":"Studio X"},"performers":[{"name":"Jane"},{"name":"Joan"}],"tags":[{"name":"feet"}],"files":[{"path":"/data/porn/x/my scene.MP4","size":1000}]}
        ]}}}
        """;

    [Fact]
    public async Task Stash_FindsTheSceneByFileName_NotByWholePath_AndReadsItsFacts()
    {
        var result = await Find(new Handler(_ => Json(TwoScenes)));

        Assert.True(result.Reached);
        Assert.Equal("Mine", result.Scene!.Title);
        Assert.Equal("Studio X", result.Scene.Studio);
        Assert.Equal("2019-02-03", result.Scene.Date);
        Assert.Equal(70, result.Scene.Rating);
        Assert.Equal(["Jane", "Joan"], result.Scene.Performers);
        Assert.Equal(["feet"], result.Scene.Tags);
        Assert.Equal("s1", result.Scene.SourceId);
    }

    [Fact]
    public async Task Stash_SendsTheNameAsAVariable_AndOnlyAReadQuery()
    {
        var handler = new Handler(_ => Json(TwoScenes));

        await Find(handler, @"C:\x\Quote ""and"" slash.mp4");

        Assert.Contains("findScenes", handler.Body);
        Assert.DoesNotContain("mutation", handler.Body, StringComparison.OrdinalIgnoreCase);
        using var sent = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.Equal("Quote \"and\" slash.mp4", sent.RootElement.GetProperty("variables").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Stash_AFileItDoesNotHave_IsReachedButHasNoScene()
    {
        var result = await Find(new Handler(_ => Json("{\"data\":{\"findScenes\":{\"scenes\":[]}}}")));

        Assert.True(result.Reached);
        Assert.Null(result.Scene);
    }

    [Fact]
    public async Task Stash_ASceneWhoseFileHasTheSameNameButAnotherSize_IsStillTheBestMatch()
    {
        var body = "{\"data\":{\"findScenes\":{\"scenes\":[{\"title\":\"Moved\",\"files\":[{\"path\":\"/d/My Scene.mp4\",\"size\":5}]}]}}}";

        var result = await Find(new Handler(_ => Json(body)));

        Assert.Equal("Moved", result.Scene!.Title);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Stash_ARefusalOrAnError_IsNotReached(HttpStatusCode code)
    {
        var result = await Find(new Handler(_ => new HttpResponseMessage(code)));

        Assert.False(result.Reached);
    }

    [Fact]
    public async Task Stash_AServerThatCannotBeReached_IsNotReached_AndDoesNotThrow()
    {
        var result = await Find(new Handler(_ => throw new HttpRequestException("down")));

        Assert.False(result.Reached);
    }

    [Fact]
    public async Task Stash_AnAnswerThatIsNotStash_IsNotReached()
    {
        Assert.False((await Find(new Handler(_ => Json("<html/>")))).Reached);
        Assert.False((await Find(new Handler(_ => Json("{\"errors\":[{\"message\":\"x\"}]}")))).Reached);
    }
}
