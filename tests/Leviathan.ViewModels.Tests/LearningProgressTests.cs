using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.ViewModels.Tests;

/// <summary>The "Learning" bar and the accuracy report: how many examples there are, and whether they hold a pattern.</summary>
public sealed class LearningProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static TriageDecisionRecord Rec(string path, TriageDecision decision, int minute = 0) => new(T0.AddMinutes(minute), path, decision, null, 1000, 1);

    private static HashSet<string> Have(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);

    // ---- the bar ----

    [Fact]
    public void Progress_CountsOnlyFilesThatHaveFacts()
    {
        var progress = LearningProgress.From(
            [Rec("a", TriageDecision.Kept), Rec("b", TriageDecision.Discarded), Rec("c", TriageDecision.Kept), Rec("d", TriageDecision.Skipped)],
            Have("a", "b", "d"));

        Assert.Equal(1, progress.Keeps);
        Assert.Equal(1, progress.Discards);
    }

    [Fact]
    public void Progress_NothingYet_IsEmpty_AndNotReady()
    {
        var progress = LearningProgress.From([], Have());

        Assert.Equal(0, progress.Fraction);
        Assert.False(progress.Ready);
    }

    [Fact]
    public void TheBar_FillsHalfForEachKind_SoOneKindAloneNeverMakesItReady()
    {
        var onlyKeeps = new LearningProgress(500, 0);
        var both = new LearningProgress(50, 50, 100);

        Assert.Equal(0.5, onlyKeeps.Fraction, 3);
        Assert.False(onlyKeeps.Ready);
        Assert.Equal(0.5, both.Fraction, 3);
    }

    [Fact]
    public void Ready_NeedsTheTargetOfBothKinds()
    {
        Assert.True(new LearningProgress(100, 100).Ready);
        Assert.Equal(1, new LearningProgress(300, 100).Fraction, 3);
        Assert.False(new LearningProgress(100, 99).Ready);
    }

    [Fact]
    public void TheWords_SayWhatIsMissing()
    {
        var progress = new LearningProgress(100, 38);

        Assert.Equal("Learning · 100 kept, 38 discarded", progress.Label);
        Assert.Contains("62 more discarded", progress.Detail);
        Assert.DoesNotContain("more kept", progress.Detail);
        Assert.Equal("Learning · enough examples", new LearningProgress(100, 100).Label);
    }

    // ---- the accuracy report ----

    private static FileFacts Facts(string path, params string[] tokens) => new(
        path, 1000, 1, T0, new NameFacts(tokens, "Working", ".mp4", null, 1080), new VideoFacts(600, 1920, 1080, "h264", 1), [], [], true);

    private static List<(TriageDecisionRecord, FileFacts)> Examples(int count, Func<int, bool> discard, Func<int, string[]> words) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            var path = $@"Z:\{i}.mp4";
            return (Rec(path, discard(i) ? TriageDecision.Discarded : TriageDecision.Kept, i), Facts(path, words(i)));
        })];

    [Fact]
    public void Report_ADataSetWithAPattern_IsRightMoreOftenThanGuessing()
    {
        // Every third file is a discard and says so in its name; the rest are keeps.
        var examples = Examples(90, i => i % 3 == 0, i => i % 3 == 0 ? ["bts", "behind"] : ["scene", "studio"]);

        var report = AccuracyReport.Evaluate(examples);

        Assert.True(report.Enough);
        Assert.Equal(1.0, report.Accuracy, 3);
        Assert.True(report.Accuracy > report.MajorityAccuracy);
        Assert.Equal(1.0, report.DiscardPrecision);
        Assert.Contains("real pattern", report.Text);
    }

    [Fact]
    public void Report_IsJudgedOnTheLatestDecisions_NotOnesItLearnedFrom()
    {
        var examples = Examples(50, i => i % 2 == 0, i => [$"unique{i}"]);

        var report = AccuracyReport.Evaluate(examples);

        Assert.Equal(40, report.Trained);
        Assert.Equal(10, report.Tested);
    }

    [Fact]
    public void Report_TooFewExamples_SaysItCannotTellYet()
    {
        var report = AccuracyReport.Evaluate(Examples(10, i => i % 2 == 0, i => ["x"]));

        Assert.False(report.Enough);
        Assert.Contains("Not enough yet", report.Text);
    }

    [Fact]
    public void Report_OneKindOnly_SaysItCannotTellYet()
    {
        var report = AccuracyReport.Evaluate(Examples(60, _ => false, i => ["x"]));

        Assert.False(report.Enough);
    }

    [Fact]
    public void Report_NoRealPattern_DoesNotClaimOne()
    {
        // Names are all different and tell nothing: it cannot beat guessing by much.
        var report = AccuracyReport.Evaluate(Examples(80, i => i % 4 == 0, i => [$"w{i}"]));

        Assert.True(report.Enough);
        Assert.DoesNotContain("real pattern", report.Text);
    }

    [Fact]
    public void Report_IgnoresSkipsAndTakenBackDecisions()
    {
        var examples = Examples(40, i => i % 2 == 0, i => ["a"]);
        examples.Add((Rec(@"Z:\skip.mp4", TriageDecision.Skipped, 99), Facts(@"Z:\skip.mp4", "a")));

        var report = AccuracyReport.Evaluate(examples);

        Assert.Equal(40, report.Trained + report.Tested);
    }

    // ---- the model ----

    [Fact]
    public void Model_LearnsWhichWordsMeanDiscard()
    {
        var model = new BaselineModel();
        for (var i = 0; i < 20; i++)
        {
            model.Add(["t:bts"], discarded: true);
            model.Add(["t:scene"], discarded: false);
        }

        Assert.True(model.DiscardProbability(["t:bts"]) > 0.9);
        Assert.True(model.DiscardProbability(["t:scene"]) < 0.1);
        Assert.Equal(0.5, model.DiscardProbability(["t:never-seen"]), 3);
    }

    [Fact]
    public void Model_WithNoExamples_IsUndecided()
    {
        Assert.Equal(0.5, new BaselineModel().DiscardProbability(["t:x"]));
    }

    [Fact]
    public void Features_CoverTheNameTheVideoAndTheServer()
    {
        var facts = new FileFacts(
            "p",
            2L * 1024 * 1024 * 1024,
            1,
            T0,
            new NameFacts(["cadence"], "Working", ".mp4", 2019, 1080),
            new VideoFacts(900, 1920, 1080, "H264", 1),
            [new SceneFacts("s", "T", "Studio X", "2018-05-06", 70, ["Jane"], ["Feet"])],
            ["s"],
            true);

        var features = LearningFeatures.Of(facts);

        Assert.Contains("t:cadence", features);
        Assert.Contains("folder:working", features);
        Assert.Contains("len:10-20", features);
        Assert.Contains("codec:h264", features);
        Assert.Contains("p:jane", features);
        Assert.Contains("g:feet", features);
        Assert.Contains("studio:studio x", features);
        Assert.Contains("year:2018", features);
    }
}
