using System.Collections.ObjectModel;
using Leviathan.Contracts;
using Leviathan.ViewModels.Hub;

namespace Leviathan.ViewModels.Tests;

/// <summary>The Operations tab (spec §12.6): groups, which actions fit which jobs, the actions on a selection, and a list that keeps its lines.</summary>
public sealed class OperationActionsTests
{
    private sealed class FakeControl : IJobControl
    {
        public List<string> Calls { get; } = [];

        public Task PauseAsync(Guid jobId) => Log("pause", jobId);

        public Task ResumeAsync(Guid jobId) => Log("resume", jobId);

        public Task CancelAsync(Guid jobId) => Log("cancel", jobId);

        public Task RemoveAsync(Guid jobId) => Log("remove", jobId);

        public Task RunNowAsync(Guid jobId) => Log("runnow", jobId);

        public Task MoveAsync(Guid jobId, int queuePosition) => Log($"move{queuePosition}", jobId);

        public Task SetPriorityAsync(Guid jobId, JobPriority priority) => Log($"priority{priority}", jobId);

        public Task PauseAllAsync() => Log("pauseall", Guid.Empty);

        public Task ResumeAllAsync() => Log("resumeall", Guid.Empty);

        public JobReport? Report { get; set; }

        public List<(Guid Job, Guid Prompt, ErrorDecision Decision)> Answers { get; } = [];

        public Task AnswerPromptAsync(Guid jobId, Guid promptId, ErrorDecision decision)
        {
            Answers.Add((jobId, promptId, decision));
            return Task.CompletedTask;
        }

        public Task<JobReport?> GetReportAsync(Guid jobId) => Task.FromResult(Report);

        private Task Log(string what, Guid id)
        {
            Calls.Add($"{what}:{id:N}"[..(what.Length + 9)]);
            return Task.CompletedTask;
        }
    }

    private static JobSnapshot Job(JobState state, Guid? id = null, int queue = -1, long done = 0, long total = -1, double speed = 0, JobPriority priority = JobPriority.Normal) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = JobKind.Copy,
        Lane = JobLane.Operations,
        Title = "Copy",
        State = state,
        Priority = priority,
        QueuePosition = queue,
        BytesDone = done,
        BytesTotal = total,
        BytesPerSecond = speed,
    };

    private static OperationsModel ModelOf(params JobSnapshot[] jobs)
    {
        var model = new OperationsModel();
        foreach (var job in jobs)
        {
            model.Apply(job);
        }

        return model;
    }

    private static string Tag(Guid id, string what) => $"{what}:{id:N}"[..(what.Length + 9)];

    [Fact]
    public void Groups_SplitRunningQueuedAndFinished_AndLeaveOutEmptyOnes()
    {
        var model = ModelOf(Job(JobState.Running), Job(JobState.Paused), Job(JobState.Queued, queue: 0), Job(JobState.Failed));

        Assert.Equal(["Running (2)", "Queued (1)", "Completed and failed (1)"], model.Groups.Select(g => g.Title));
        Assert.Equal(OperationGroupKind.Finished, model.Groups[^1].Kind);
        Assert.Equal(["Queued (1)"], ModelOf(Job(JobState.Queued, queue: 0)).Groups.Select(g => g.Title));
        Assert.Empty(ModelOf().Groups);
    }

    [Theory]
    [InlineData(JobState.Running, true, false, false, true, false)]
    [InlineData(JobState.Queued, true, false, true, true, true)]
    [InlineData(JobState.Paused, false, true, true, true, true)]
    [InlineData(JobState.Waiting, true, false, true, true, true)]
    [InlineData(JobState.Completed, false, false, false, false, true)]
    [InlineData(JobState.Failed, false, false, false, false, true)]
    public void AnEntry_OffersTheActionsThatFitItsState(JobState state, bool pause, bool resume, bool runNow, bool cancel, bool remove)
    {
        var entry = ModelOf(Job(state, queue: state == JobState.Queued ? 1 : -1)).Entries.Single();

        Assert.Equal(pause, entry.CanPause);
        Assert.Equal(resume, entry.CanResume);
        Assert.Equal(runNow, entry.CanRunNow);
        Assert.Equal(cancel, entry.CanCancel);
        Assert.Equal(remove, entry.CanRemove);
    }

    [Fact]
    public void MoveUp_IsNotOfferedToTheFirstInTheQueue_AndMoveDownOnlyToQueuedJobs()
    {
        var model = ModelOf(Job(JobState.Queued, queue: 0), Job(JobState.Queued, queue: 1), Job(JobState.Running));
        var entries = model.Entries;

        var first = entries.Single(e => e.QueuePosition == 0);
        var second = entries.Single(e => e.QueuePosition == 1);
        var running = entries.Single(e => e.State == JobState.Running);
        Assert.False(first.CanMoveUp);
        Assert.True(first.CanMoveDown);
        Assert.True(second.CanMoveUp);
        Assert.False(running.CanMoveUp);
        Assert.False(running.CanMoveDown);
    }

    [Fact]
    public async Task Pause_OnAMixedSelection_TouchesOnlyTheJobsItFits()
    {
        var running = Guid.NewGuid();
        var done = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var model = ModelOf(Job(JobState.Running, running), Job(JobState.Completed, done), Job(JobState.Queued, queued, queue: 0));
        var control = new FakeControl();

        var touched = await new OperationActions(control).RunAsync(OperationAction.Pause, model.Entries);

        Assert.Equal(2, touched);
        Assert.Contains(Tag(running, "pause"), control.Calls);
        Assert.Contains(Tag(queued, "pause"), control.Calls);
        Assert.DoesNotContain(Tag(done, "pause"), control.Calls);
    }

    [Fact]
    public async Task MoveUp_OfSeveralJobs_GoesEarlierFirst_SoTheyDoNotStepOverEachOther()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var model = ModelOf(Job(JobState.Queued, a, queue: 2), Job(JobState.Queued, b, queue: 3));
        var control = new FakeControl();

        await new OperationActions(control).RunAsync(OperationAction.MoveUp, model.Entries);

        Assert.Equal([Tag(a, "move1"), Tag(b, "move2")], control.Calls);
    }

    [Fact]
    public async Task MoveDown_OfSeveralJobs_GoesLaterFirst()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var model = ModelOf(Job(JobState.Queued, a, queue: 0), Job(JobState.Queued, b, queue: 1));
        var control = new FakeControl();

        await new OperationActions(control).RunAsync(OperationAction.MoveDown, model.Entries);

        Assert.Equal([Tag(b, "move2"), Tag(a, "move1")], control.Calls);
    }

    [Fact]
    public async Task SetPriority_SkipsFinishedJobsAndJobsThatHaveIt()
    {
        var queued = Guid.NewGuid();
        var model = ModelOf(Job(JobState.Queued, queued, queue: 0), Job(JobState.Completed), Job(JobState.Queued, queue: 1, priority: JobPriority.High));
        var control = new FakeControl();

        var touched = await new OperationActions(control).SetPriorityAsync(JobPriority.High, model.Entries);

        Assert.Equal(1, touched);
        Assert.Equal([Tag(queued, "priorityHigh")], control.Calls);
    }

    [Fact]
    public void CanDo_IsFalseWhenNothingSelectedFits()
    {
        var model = ModelOf(Job(JobState.Completed));

        Assert.False(OperationActions.CanDo(OperationAction.Pause, model.Entries));
        Assert.True(OperationActions.CanDo(OperationAction.Remove, model.Entries));
        Assert.False(OperationActions.CanDo(OperationAction.Cancel, []));
    }

    [Fact]
    public void QueueEta_NeedsTwoUnfinishedJobsWithKnownSizesAndASpeed()
    {
        Assert.Null(ModelOf(Job(JobState.Running, done: 0, total: 1000, speed: 10)).QueueEta);
        Assert.Null(ModelOf(Job(JobState.Running, total: 1000, speed: 10), Job(JobState.Queued, queue: 0)).QueueEta);
        Assert.Null(ModelOf(Job(JobState.Running, total: 1000), Job(JobState.Queued, queue: 0, total: 1000)).QueueEta);

        var eta = ModelOf(Job(JobState.Running, total: 600_000_000, speed: 1_000_000), Job(JobState.Queued, queue: 0, total: 600_000_000)).QueueEta;

        Assert.Equal("Everything finishes in about 20 min", eta);
    }

    [Fact]
    public void Sync_BuildsHeadersAndRows_ThenKeepsTheRowsItAlreadyHas()
    {
        var running = Guid.NewGuid();
        var model = ModelOf(Job(JobState.Running, running, done: 10, total: 100));
        var lines = new ObservableCollection<OperationListItem>();
        OperationLines.Sync(lines, model.Groups);
        var row = Assert.IsType<OperationRow>(lines[1]);
        var changes = 0;
        lines.CollectionChanged += (_, _) => changes++;

        model.Apply(Job(JobState.Running, running, done: 50, total: 100));
        OperationLines.Sync(lines, model.Groups);

        Assert.Same(row, lines[1]);
        Assert.Equal(50, row.Entry.Percent);
        Assert.Equal(0, changes);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Sync_WhenAJobFinishes_MovesItToTheFinishedGroupAndDropsTheEmptyOne()
    {
        var id = Guid.NewGuid();
        var model = ModelOf(Job(JobState.Running, id), Job(JobState.Queued, queue: 0));
        var lines = new ObservableCollection<OperationListItem>();
        OperationLines.Sync(lines, model.Groups);

        model.Apply(Job(JobState.Completed, id));
        OperationLines.Sync(lines, model.Groups);

        Assert.Equal(["group:Queued", lines[1].Key, "group:Finished", id.ToString("N")], lines.Select(l => l.Key));
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void Sync_WhenEverythingIsCleared_EmptiesTheList()
    {
        var model = ModelOf(Job(JobState.Completed));
        var lines = new ObservableCollection<OperationListItem>();
        OperationLines.Sync(lines, model.Groups);

        model.ClearFinished();
        OperationLines.Sync(lines, model.Groups);

        Assert.Empty(lines);
    }

    private static JobSnapshot Prompted(Guid id, Guid promptId) => Job(JobState.Running, id) with { Prompt = new JobPrompt(promptId, ErrorKind.AccessDenied, @"C:\a\b.txt", "Access is denied") };

    [Fact]
    public void AJobWithAQuestion_AsksForAttention_AndSaysWhat()
    {
        var entry = ModelOf(Prompted(Guid.NewGuid(), Guid.NewGuid())).Entries.Single();

        Assert.True(entry.HasPrompt);
        Assert.True(entry.NeedsAttention);
        Assert.Equal("Needs an answer: Access is denied", entry.Detail);
        Assert.Contains(@"C:\a\b.txt", entry.PromptText);
    }

    [Theory]
    [InlineData(ErrorDecision.Skip)]
    [InlineData(ErrorDecision.Retry)]
    [InlineData(ErrorDecision.SkipAllLikeThis)]
    public async Task Answer_QuotesTheQuestionOfTheJob(ErrorDecision decision)
    {
        var job = Guid.NewGuid();
        var prompt = Guid.NewGuid();
        var control = new FakeControl();

        var sent = await new OperationActions(control).AnswerAsync(ModelOf(Prompted(job, prompt)).Entries.Single(), decision);

        Assert.True(sent);
        Assert.Equal([(job, prompt, decision)], control.Answers);
    }

    [Fact]
    public async Task Answer_ToAJobWithoutAQuestion_DoesNothing()
    {
        var control = new FakeControl();

        var sent = await new OperationActions(control).AnswerAsync(ModelOf(Job(JobState.Running)).Entries.Single(), ErrorDecision.Skip);

        Assert.False(sent);
        Assert.Empty(control.Answers);
    }

    [Fact]
    public void AWaitingJob_CountsDownToItsNextTry_AndTheCountdownFollowsTheClock()
    {
        var clock = new Leviathan.Testing.ManualTimeProvider();
        var model = new OperationsModel(clock);
        model.Apply(Job(JobState.Waiting) with { WaitingReason = @"Waiting for \nas", RetryAt = clock.GetUtcNow().AddSeconds(8) });

        Assert.Equal(@"Waiting for \nas, retrying in 8 s", model.Entries.Single().Detail);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(@"Waiting for \nas, retrying in 5 s", model.Entries.Single().Detail);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(@"Waiting for \nas, retrying now", model.Entries.Single().Detail);
    }

    [Fact]
    public void ARunningJob_HasNoRetryTime_EvenIfTheSnapshotCarriesOne()
    {
        var model = ModelOf(Job(JobState.Running) with { RetryAt = DateTimeOffset.UtcNow.AddSeconds(5) });

        Assert.Null(model.Entries.Single().RetryAt);
    }

    private static JobReport ReportOf(JobKind kind, string? destination, params ItemOutcome[] items) =>
        new(Guid.NewGuid(), "Copy 3 items", kind, JobState.Failed, DateTimeOffset.UtcNow, 2048, items.Count(i => i.Status == ItemStatus.Done), items.Count(i => i.Status == ItemStatus.Skipped), items.Count(i => i.Status == ItemStatus.Failed), items, ["Renamed a:b to a_b"], null, destination);

    [Fact]
    public void ReportView_ListsFailuresFirst_WithTheirReasons_AndCountsThem()
    {
        var view = new OperationReportView(ReportOf(
            JobKind.Copy, @"\nas\media",
            new ItemOutcome(@"C:\in\ok.txt", @"\nas\media\ok.txt", ItemStatus.Done),
            new ItemOutcome(@"C:\in\locked.txt", null, ItemStatus.Failed, "Access is denied"),
            new ItemOutcome(@"C:\in\same.txt", null, ItemStatus.Skipped)));

        Assert.Equal(["locked.txt", "same.txt", "ok.txt"], view.Lines.Select(l => l.Name));
        Assert.Equal("Access is denied", view.Lines[0].Detail);
        Assert.StartsWith("1 done · 1 skipped · 1 failed · ", view.Summary);
        Assert.Equal([@"C:\in\locked.txt"], view.FailedSources);
        Assert.Equal(@"C:\in", view.SourceFolder);
    }

    [Theory]
    [InlineData(JobKind.Copy, @"\nas\media", true, true)]
    [InlineData(JobKind.Copy, null, true, false)]
    [InlineData(JobKind.Delete, null, true, true)]
    [InlineData(JobKind.Copy, @"\nas\media", false, false)]
    [InlineData(JobKind.Extract, @"C:\out", true, false)]
    public void ReportView_OffersRetryOnlyWhenSomethingFailedAndTheJobCanRunAgain(JobKind kind, string? destination, bool failed, bool expected)
    {
        var view = new OperationReportView(ReportOf(kind, destination, new ItemOutcome(@"C:\in\a.txt", null, failed ? ItemStatus.Failed : ItemStatus.Done, failed ? "denied" : null)));

        Assert.Equal(expected, view.CanRetryFailed);
    }

    [Fact]
    public async Task LoadReport_GivesTheViewOfWhatTheWorkerKept_OrNullWhenItHasNone()
    {
        var control = new FakeControl();
        var actions = new OperationActions(control);

        Assert.Null(await actions.LoadReportAsync(Guid.NewGuid()));
        control.Report = ReportOf(JobKind.Delete, null, new ItemOutcome(@"C:\in\a.txt", null, ItemStatus.Failed, "denied"));

        var view = await actions.LoadReportAsync(Guid.NewGuid());

        Assert.NotNull(view);
        Assert.Contains("denied", view.ToText());
    }
}
