using Leviathan.Testing;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.ViewModels.Tests;

/// <summary>The hint for the file on screen (slice 5): shown only when the examples are enough and the model is clearly better than guessing.</summary>
public sealed class LearningAdvisorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class Store : IFileFactStore
    {
        public Dictionary<string, FileFacts> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<FileFacts?> GetAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(path));

        public Task SaveAsync(FileFacts facts, CancellationToken cancellationToken)
        {
            Items[facts.Path] = facts;
            return Task.CompletedTask;
        }

        public Task<(int Complete, int Incomplete)> CountAsync(CancellationToken cancellationToken) =>
            Task.FromResult((Items.Values.Count(f => f.Complete), Items.Values.Count(f => !f.Complete)));

        public Task<ISet<string>> CompletePathsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ISet<string>>(new HashSet<string>(Items.Values.Where(f => f.Complete).Select(f => f.Path), StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<FileFacts>> LoadAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FileFacts>>([.. Items.Values]);
    }

    private sealed class Video : IVideoFactsSource
    {
        public Task<VideoFacts?> GetAsync(string path, long size, long modifiedTicks, CancellationToken cancellationToken) =>
            Task.FromResult<VideoFacts?>(new VideoFacts(600, 1920, 1080, "h264", 1));
    }

    private sealed class NoScenes : ISceneLookup
    {
        public Task<SceneLookupResult> FindAsync(LearningSource source, string? secret, string path, long size, CancellationToken cancellationToken) =>
            Task.FromResult(new SceneLookupResult(true, null));
    }

    private sealed class NoSecrets : ILearningSecrets
    {
        public string? Get(string sourceId) => null;

        public void Set(string sourceId, string? secret) => throw new NotSupportedException();
    }

    // Half the files are discards and say "bts" in the name; the rest are keeps and say "scene".
    private static LearningAdvisor Advisor(int count, Func<int, bool> discard, Func<int, string[]> words)
    {
        var store = new Store();
        var history = new FakeTriageHistory();
        for (var i = 0; i < count; i++)
        {
            var path = $@"Z:\Working\{i}.mp4";
            history.AppendAsync(new TriageDecisionRecord(T0.AddMinutes(i), path, discard(i) ? TriageDecision.Discarded : TriageDecision.Kept, null, 1000, 1), CancellationToken.None).GetAwaiter().GetResult();
            store.Items[path] = new FileFacts(path, 1000, 1, T0, new NameFacts(words(i), "Working", ".mp4", null, 1080), new VideoFacts(600, 1920, 1080, "h264", 1), [], [], true);
        }

        var collector = new FeatureCollector(store, new Video(), new NoScenes(), () => new LearningSettings { Enabled = true }, new NoSecrets(), history, new ManualTimeProvider(T0));
        return new LearningAdvisor(history, store, collector);
    }

    private static LearningAdvisor Patterned(int count = 240) =>
        Advisor(count, i => i % 2 == 0, i => i % 2 == 0 ? ["bts", "behind", $"n{i}"] : ["scene", "studio", $"n{i}"]);

    [Fact]
    public async Task Hint_AFileLikeTheDiscards_SaysDiscardAndWhy()
    {
        var hint = await Patterned().HintAsync(@"Z:\Working\bts new clip.mp4", 1000, 1, CancellationToken.None);

        Assert.NotNull(hint);
        Assert.False(hint.Keep);
        Assert.StartsWith("Discard ", hint.Label);
        Assert.Contains("the name has \"bts\"", hint.Reasons);
        Assert.Contains("Because", hint.Detail);
        Assert.True(hint.Confidence > 0.5 && hint.Confidence <= 1);
    }

    [Fact]
    public async Task Hint_AFileLikeTheKeeps_SaysKeep()
    {
        var hint = await Patterned().HintAsync(@"Z:\Working\scene studio fresh.mp4", 1000, 1, CancellationToken.None);

        Assert.NotNull(hint);
        Assert.True(hint.Keep);
        Assert.StartsWith("Keep ", hint.Label);
    }

    [Fact]
    public async Task Hint_TooFewExamples_ShowsNothing()
    {
        var hint = await Patterned(40).HintAsync(@"Z:\Working\bts.mp4", 1000, 1, CancellationToken.None);

        Assert.Null(hint);
    }

    [Fact]
    public async Task Hint_NoPatternInTheFiles_ShowsNothing()
    {
        // Every name is different and says nothing about the decision: no better than guessing, so no hint.
        var advisor = Advisor(240, i => i % 2 == 0, i => [$"w{i}"]);

        var hint = await advisor.HintAsync(@"Z:\Working\bts.mp4", 1000, 1, CancellationToken.None);

        Assert.Null(hint);
    }

    [Fact]
    public async Task Describe_SaysWhichModelAndHowItDid()
    {
        var text = await Patterned().DescribeAsync(CancellationToken.None);

        Assert.Contains("model", text);
        Assert.Contains("real pattern", text);
    }

    [Fact]
    public async Task Describe_TooFewExamples_SaysItCannotTellYet()
    {
        Assert.Contains("Not enough yet", await Patterned(10).DescribeAsync(CancellationToken.None));
    }

    [Fact]
    public void Logistic_LearnsWhichFeaturesMeanDiscard_AndWhyCountsBothDirections()
    {
        var examples = new List<(IReadOnlyCollection<string>, bool)>();
        for (var i = 0; i < 40; i++)
        {
            examples.Add((["t:bts", "folder:x"], true));
            examples.Add((["t:scene", "folder:x"], false));
        }

        var model = LogisticModel.Train(examples);

        Assert.True(model.DiscardProbability(["t:bts"]) > 0.8);
        Assert.True(model.DiscardProbability(["t:scene"]) < 0.2);
        Assert.Equal(0.5, model.DiscardProbability(["t:never-seen"]), 1);
        Assert.Contains(model.Contributions(["t:bts", "t:other"]), c => c.Feature == "t:bts" && c.Weight > 0);
        Assert.DoesNotContain(model.Contributions(["t:other"]), c => c.Feature == "t:other");
    }

    [Fact]
    public void Logistic_SameExamples_GiveTheSameAnswer()
    {
        var examples = Enumerable.Range(0, 60).Select(i => ((IReadOnlyCollection<string>)new[] { "t:" + (i % 5), "t:" + (i % 7) }, i % 3 == 0)).ToList();

        var a = LogisticModel.Train(examples).DiscardProbability(["t:1", "t:2"]);
        var b = LogisticModel.Train(examples).DiscardProbability(["t:1", "t:2"]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Describe_Features_ReadAsWords()
    {
        Assert.Equal("the name has \"bts\"", LearningFeatures.Describe("t:bts"));
        Assert.Equal("performer jane", LearningFeatures.Describe("p:jane"));
        Assert.Equal("tag feet", LearningFeatures.Describe("g:feet"));
        Assert.Equal("odd", LearningFeatures.Describe("odd"));
    }

    [Fact]
    public void Settings_ShowHint_IsOnByDefault_AndKeptWhenCleaned()
    {
        Assert.True(new LearningSettings().ShowHint);
        Assert.False((new LearningSettings { ShowHint = false }).Cleaned().ShowHint);
    }
}
