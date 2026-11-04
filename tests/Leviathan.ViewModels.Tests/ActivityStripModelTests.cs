using Leviathan.Contracts;
using Leviathan.ViewModels.Hub;

namespace Leviathan.ViewModels.Tests;

public sealed class ActivityStripModelTests
{
    private static JobSnapshot Job(
        JobState state, int color = 0, long done = 0, long total = -1, string title = "Copy", string? error = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = JobKind.Copy,
        Lane = JobLane.Operations,
        Title = title,
        State = state,
        Priority = JobPriority.Normal,
        ColorIndex = color,
        BytesDone = done,
        BytesTotal = total,
        Error = error,
    };

    private static (OperationsModel Operations, ActivityStripModel Strip) Make(params JobSnapshot[] jobs)
    {
        var operations = new OperationsModel();
        foreach (var job in jobs)
        {
            operations.Apply(job);
        }

        return (operations, new ActivityStripModel(operations));
    }

    [Fact]
    public void Lines_WhenNothingRuns_AreEmpty_SoTheStripTakesNoSpace() =>
        Assert.Empty(Make().Strip.Lines);

    [Fact]
    public void Lines_ARunningJob_FillsInProportion_InItsOwnColour()
    {
        var (_, strip) = Make(Job(JobState.Running, color: 3, done: 25, total: 100));

        var line = Assert.Single(strip.Lines);

        Assert.Equal(3, line.ColorIndex);
        Assert.Equal(0.25, line.Fraction, 3);
        Assert.Equal(StripLineStyle.Solid, line.Style);
        Assert.False(line.RedTip);
    }

    [Fact]
    public void Lines_WhileTheTotalIsUnknown_Shimmer() =>
        Assert.Equal(StripLineStyle.Shimmer, Assert.Single(Make(Job(JobState.Running, done: 10)).Strip.Lines).Style);

    [Fact]
    public void Lines_PausedIsDashed_AndWaitingForAShareIsPulsing()
    {
        var (_, strip) = Make(Job(JobState.Paused, color: 0, done: 5, total: 10), Job(JobState.Waiting, color: 1, done: 5, total: 10));

        Assert.Equal([StripLineStyle.Dashed, StripLineStyle.Pulse], strip.Lines.Select(l => l.Style).OrderBy(s => s));
        Assert.Equal(StripLineStyle.Dashed, strip.Lines.Single(l => l.ColorIndex == 0).Style);
        Assert.Equal(StripLineStyle.Pulse, strip.Lines.Single(l => l.ColorIndex == 1).Style);
    }

    [Fact]
    public void Lines_QueuedJobs_HaveNoLineYet() =>
        Assert.Empty(Make(Job(JobState.Queued, total: 100)).Strip.Lines);

    [Fact]
    public void Lines_ARunningJobWithFailedItems_HasARedTip()
    {
        var (_, strip) = Make(Job(JobState.Running, done: 5, total: 10, error: "2 items failed"));

        Assert.True(Assert.Single(strip.Lines).RedTip);
    }

    [Fact]
    public void Lines_AFailedJob_KeepsItsRedTip_UntilTheFailureIsSeen()
    {
        var (operations, strip) = Make(Job(JobState.Failed, done: 5, total: 10, error: "disk full"));

        Assert.True(Assert.Single(strip.Lines).RedTip);

        operations.MarkFailuresSeen();

        Assert.Empty(strip.Lines);
    }

    [Fact]
    public void Lines_ACompletedJob_LeavesTheStrip()
    {
        var id = Guid.NewGuid();
        var (operations, strip) = Make(Job(JobState.Running, done: 5, total: 10, id: id));

        operations.Apply(Job(JobState.Completed, done: 10, total: 10, id: id));

        Assert.Empty(strip.Lines);
    }

    [Fact]
    public void Lines_UpToFourJobs_EachGetTheirOwnLine()
    {
        var (_, strip) = Make(Enumerable.Range(0, 4).Select(i => Job(JobState.Running, color: i, done: 1, total: 10)).ToArray());

        Assert.Equal(4, strip.Lines.Count);
        Assert.All(strip.Lines, l => Assert.Equal(0, l.MergedCount));
    }

    [Fact]
    public void Lines_MoreThanFourJobs_MergeTheExtraOnesIntoTheLastSlot_WithACount()
    {
        var jobs = Enumerable.Range(0, 7).Select(i => Job(JobState.Running, color: i, done: i * 10, total: 100, title: $"Job {i}")).ToArray();
        var (_, strip) = Make(jobs);

        var lines = strip.Lines;

        Assert.Equal(ActivityStripModel.MaxLines, lines.Count);
        var merged = lines[^1];
        Assert.Equal(4, merged.MergedCount);
        Assert.Null(merged.JobId);
        Assert.Equal(-1, merged.ColorIndex);
        Assert.All(lines.Take(3), l => Assert.NotNull(l.JobId));
        Assert.Equal(4, merged.Tooltip.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void Lines_AMergedLine_ShowsTheRedTip_WhenAnyOfItsJobsHasFailures()
    {
        var jobs = Enumerable.Range(0, 5).Select(i => Job(JobState.Running, color: i, done: 1, total: 10, error: i == 0 ? "x" : null)).ToArray(); // the oldest job lists last, so it lands in the merged slot

        Assert.True(Make(jobs).Strip.Lines[^1].RedTip);
    }

    [Fact]
    public void Tooltip_NamesTheJob_WithPercentBytesSpeedAndTimeLeft()
    {
        var job = Job(JobState.Running, done: 512L * 1024 * 1024, total: 1024L * 1024 * 1024, title: "Copy 3 items to \\\\nas\\media") with
        {
            BytesPerSecond = 100 * 1024 * 1024,
            Remaining = TimeSpan.FromMinutes(5),
        };

        var tooltip = Assert.Single(Make(job).Strip.Lines).Tooltip;

        Assert.StartsWith("Copy 3 items to \\\\nas\\media", tooltip, StringComparison.Ordinal);
        Assert.Contains("50%", tooltip, StringComparison.Ordinal);
        Assert.Contains("/s", tooltip, StringComparison.Ordinal);
        Assert.Contains("5 min left", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void JobPalette_HasEightDistinctColours_NoneRed()
    {
        var colours = Enumerable.Range(0, JobPalette.Count).Select(JobPalette.Argb).ToList();

        Assert.Equal(JobPalette.Count, colours.Distinct().Count());
        Assert.DoesNotContain(JobPalette.Failure, colours);
        Assert.Equal(JobPalette.Neutral, JobPalette.Argb(-1));
        Assert.Equal(JobPalette.Neutral, JobPalette.Argb(8));
    }
}
