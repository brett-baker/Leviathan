using Leviathan.Contracts;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Hub;

namespace Leviathan.ViewModels.Tests;

public sealed class BackgroundModelTests
{
    private static JobSnapshot Job(JobState state, string title = "Count C:\\a", JobLane lane = JobLane.Background, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = JobKind.ComputeFolderSize,
        Lane = lane,
        Title = title,
        State = state,
        Priority = JobPriority.Normal,
    };

    [Fact]
    public void OnlyBackgroundJobs_AreListed()
    {
        var model = new BackgroundModel();

        model.Apply(Job(JobState.Running, lane: JobLane.Operations));
        model.Apply(Job(JobState.Running, "Count C:\\a"));

        Assert.Equal(["Count C:\\a"], model.View().Tasks.Select(t => t.Title));
    }

    [Fact]
    public void Tasks_ListRunningFirst_ThenWaiting_Paused_Queued_AndFinishedLast()
    {
        var model = new BackgroundModel();
        model.Apply(Job(JobState.Completed, "done"));
        model.Apply(Job(JobState.Queued, "queued"));
        model.Apply(Job(JobState.Paused, "paused"));
        model.Apply(Job(JobState.Running, "running"));
        model.Apply(Job(JobState.Waiting, "waiting"));

        Assert.Equal(["running", "waiting", "paused", "queued", "done"], model.View().Tasks.Select(t => t.Title));
    }

    [Fact]
    public void ARunningJob_CanBePaused_AndAPausedOneResumed()
    {
        var model = new BackgroundModel();
        model.Apply(Job(JobState.Running, "r"));
        model.Apply(Job(JobState.Paused, "p"));
        model.Apply(Job(JobState.Completed, "c"));

        var tasks = model.View().Tasks.ToDictionary(t => t.Title);
        Assert.True(tasks["r"].CanPause);
        Assert.False(tasks["r"].CanResume);
        Assert.True(tasks["p"].CanResume);
        Assert.False(tasks["p"].CanPause);
        Assert.False(tasks["c"].CanPause);
        Assert.False(tasks["c"].CanResume);
    }

    [Fact]
    public void TheFolderSizeLine_CountsRunningQueuedAndPaused_AndTheRateOverTheLastMinute()
    {
        var time = new ManualTimeProvider();
        var model = new BackgroundModel(time: time);
        var id = Guid.NewGuid();
        model.Apply(Job(JobState.Running, id: id));
        model.Apply(Job(JobState.Queued));
        model.Apply(Job(JobState.Queued));
        model.Apply(Job(JobState.Paused));

        Assert.Equal("1 running · 2 queued · 1 paused · 0 finished in the last minute", model.View().FolderSizes);

        model.Apply(Job(JobState.Completed, id: id));
        Assert.EndsWith("1 finished in the last minute", model.View().FolderSizes, StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.EndsWith("0 finished in the last minute", model.View().FolderSizes, StringComparison.Ordinal);
    }

    [Fact]
    public void AJobReportedFinishedTwice_IsCountedOnce()
    {
        var model = new BackgroundModel();
        var id = Guid.NewGuid();

        model.Apply(Job(JobState.Completed, id: id));
        model.Apply(Job(JobState.Completed, id: id));

        Assert.Equal("Idle · 1 folder counted in the last minute", model.View().FolderSizes);
    }

    [Fact]
    public void WhenNothingRuns_TheLineSaysIdle_AndNoControlsAreNeeded()
    {
        var view = new BackgroundModel().View();

        Assert.Equal("Idle", view.FolderSizes);
        Assert.False(view.AnyActive);
        Assert.False(view.AllPaused);
    }

    [Fact]
    public void AllPaused_IsTrueOnlyWhenEveryActiveJobIsPaused()
    {
        var model = new BackgroundModel();
        model.Apply(Job(JobState.Paused));
        Assert.True(model.View().AllPaused);

        model.Apply(Job(JobState.Queued));
        Assert.False(model.View().AllPaused);
    }

    [Fact]
    public void RunningOrQueuedIds_AndPausedIds_SayWhatPauseAllAndResumeAllAct_On()
    {
        var model = new BackgroundModel();
        var running = Job(JobState.Running);
        var queued = Job(JobState.Queued);
        var paused = Job(JobState.Paused);
        model.Apply(running);
        model.Apply(queued);
        model.Apply(paused);
        model.Apply(Job(JobState.Completed));

        Assert.Equivalent(new[] { running.Id, queued.Id }, model.RunningOrQueuedIds());
        Assert.Equal([paused.Id], model.PausedIds());
    }

    [Fact]
    public void ABigQueue_ShowsOnlyTheFirstRows_ButCountsThemAll()
    {
        var model = new BackgroundModel();
        for (var i = 0; i < 500; i++)
        {
            model.Apply(Job(JobState.Queued, $"job {i}"));
        }

        var view = model.View();
        Assert.Equal(BackgroundModel.RowLimit, view.Tasks.Count);
        Assert.StartsWith("500 queued", view.FolderSizes, StringComparison.Ordinal);
    }

    [Fact]
    public void FinishedJobs_AreKeptInSmallNumber()
    {
        var model = new BackgroundModel();
        for (var i = 0; i < 20; i++)
        {
            model.Apply(Job(JobState.Completed, $"done {i}"));
        }

        Assert.Equal(BackgroundModel.FinishedKept, model.View().Tasks.Count);
    }

    [Fact]
    public void TheDetail_SaysWhereAJobIs()
    {
        var model = new BackgroundModel();
        model.Apply(Job(JobState.Running, "r") with { CurrentItem = @"C:\media\films" });
        model.Apply(Job(JobState.Queued, "q") with { QueuePosition = 2 });

        var tasks = model.View().Tasks.ToDictionary(t => t.Title);
        Assert.Equal(@"C:\media\films", tasks["r"].Detail);
        Assert.Equal("Queued, number 3", tasks["q"].Detail);
    }

    [Fact]
    public void ThumbnailsAndProcesses_AreReadWhenTheViewIsBuilt()
    {
        var stats = new ThumbnailStats(1200, 30 * 1024 * 1024, 64 * 1024 * 1024, 3, 2);
        var restarts = 0;
        var model = new BackgroundModel(() => stats, [() => new ProcessEntry("Worker", "Running", 4242, restarts, "Copies and sizes")]);

        Assert.Contains("1,200 thumbnails held", model.View().Thumbnails, StringComparison.Ordinal);
        Assert.Contains("3 being made", model.View().Thumbnails, StringComparison.Ordinal);
        Assert.Contains("2 files could not be drawn", model.View().Thumbnails, StringComparison.Ordinal);
        Assert.Equal(0, model.View().Processes[0].Restarts);

        restarts = 2;
        Assert.Equal(2, model.View().Processes[0].Restarts);
        Assert.Equal("Not in use", new BackgroundModel().View().Thumbnails);
    }

    [Fact]
    public void Changed_IsRaisedForABackgroundJob_NotForAnOperation()
    {
        var model = new BackgroundModel();
        var count = 0;
        model.Changed += (_, _) => count++;

        model.Apply(Job(JobState.Running));
        model.Apply(Job(JobState.Running, lane: JobLane.Operations));

        Assert.Equal(1, count);
    }
}
