using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Engine.Tests;

public class JobHostTests
{
    /// <summary>An executor whose behaviour each test scripts.</summary>
    private sealed class ScriptedExecutor(Func<JobContext, CancellationToken, Task<JobOutcome>> run) : IJobExecutor
    {
        public int Runs;

        public bool CanExecute(JobKind kind) => kind == JobKind.Copy;

        public Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            return run(context, cancellationToken);
        }
    }

    private static JobRequest CopyRequest(bool paused = false) => new(JobKind.Copy, [@"D:\a"], @"\\nas\media") { StartPaused = paused };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task SubmittedJob_RunsToCompletion_AndReportsProgress()
    {
        var executor = new ScriptedExecutor((ctx, _) =>
        {
            ctx.Report(50, 100, 1, 2, "a.mkv");
            ctx.Report(100, 100, 2, 2, "b.mkv");
            return Task.FromResult(JobOutcome.Completed);
        });
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        var snapshots = new List<JobSnapshot>();
        host.JobChanged += (_, s) => { lock (snapshots) { snapshots.Add(s); } };
        host.Start();

        var job = host.Submit(CopyRequest(), JobLane.Operations, @"\\nas", "Copy a");
        await WaitUntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Completed);

        var final = host.Snapshot(host.Scheduler.Find(job.Id)!);
        Assert.Equal(100, final.BytesDone);
        Assert.Equal(2, final.ItemsDone);
        lock (snapshots)
        {
            Assert.Contains(snapshots, s => s.State == JobState.Running);
        }
    }

    [Fact]
    public async Task UnsupportedKind_FailsWithMessage()
    {
        await using var host = new JobHost(new JobScheduler(), [], new InMemoryJobJournal());
        host.Start();

        var job = host.Submit(CopyRequest(), JobLane.Operations, null, "Copy");
        await WaitUntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished);

        var finished = host.Scheduler.Find(job.Id)!;
        Assert.Equal(JobState.Failed, finished.State);
        Assert.Contains("not supported", finished.Error);
    }

    [Fact]
    public async Task Cancel_StopsRunningExecutor()
    {
        var started = new TaskCompletionSource();
        var executor = new ScriptedExecutor(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return JobOutcome.Completed;
        });
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        host.Start();

        var job = host.Submit(CopyRequest(), JobLane.Operations, null, "Copy");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Cancel(job.Id);

        await WaitUntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Cancelled);
    }

    [Fact]
    public async Task Pause_StopsExecutor_AndResume_RunsItAgain()
    {
        var firstRun = new TaskCompletionSource();
        var executor = new ScriptedExecutor(async (_, ct) =>
        {
            if (firstRun.TrySetResult())
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return JobOutcome.Completed;
        });
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        host.Start();

        var job = host.Submit(CopyRequest(), JobLane.Operations, null, "Copy");
        await firstRun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Pause(job.Id);
        await WaitUntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Paused && executor.Runs == 1);
        await Task.Delay(50);
        Assert.Equal(JobState.Paused, host.Scheduler.Find(job.Id)!.State);

        host.Resume(job.Id);
        await WaitUntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Completed);
        Assert.Equal(2, executor.Runs);
    }

    [Fact]
    public async Task TransientNetworkFailure_MovesJobToWaiting_ThenRetries()
    {
        var attempts = 0;
        var executor = new ScriptedExecutor((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new LocationException(LocationError.Unreachable, LocationPath.Parse(@"\\nas\media"), "nas is not responding");
            }

            return Task.FromResult(JobOutcome.Completed);
        });
        var fastRetry = new BackoffPolicy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), 0);
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal(), retryBackoff: fastRetry);
        var sawWaiting = false;
        host.JobChanged += (_, s) => sawWaiting |= s.State == JobState.Waiting;
        host.Start();

        var job = host.Submit(CopyRequest(), JobLane.Operations, @"\\nas", "Copy");
        await WaitUntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Completed);

        Assert.True(sawWaiting);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecutorCrash_FailsJob_WithoutStoppingHost()
    {
        var calls = 0;
        var executor = new ScriptedExecutor((_, _) =>
            Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : Task.FromResult(JobOutcome.Completed));
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        host.Start();

        var bad = host.Submit(CopyRequest(), JobLane.Operations, null, "bad");
        await WaitUntilAsync(() => host.Scheduler.Find(bad.Id)!.IsFinished);
        var good = host.Submit(CopyRequest(), JobLane.Operations, null, "good");
        await WaitUntilAsync(() => host.Scheduler.Find(good.Id)!.IsFinished);

        Assert.Equal(JobState.Failed, host.Scheduler.Find(bad.Id)!.State);
        Assert.Equal("boom", host.Scheduler.Find(bad.Id)!.Error);
        Assert.Equal(JobState.Completed, host.Scheduler.Find(good.Id)!.State);
    }

    [Fact]
    public async Task Start_RecoversUnfinishedJobsFromJournal()
    {
        var journal = new InMemoryJobJournal();
        var interruptedId = Guid.NewGuid();
        var pausedId = Guid.NewGuid();
        var doneId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        journal.Append(new JournalRecord { Type = JournalRecordType.Enqueued, JobId = interruptedId, At = at, Request = CopyRequest(), Lane = JobLane.Operations, Title = "interrupted" });
        journal.Append(new JournalRecord { Type = JournalRecordType.StateChanged, JobId = interruptedId, At = at, State = JobState.Running });
        journal.Append(new JournalRecord { Type = JournalRecordType.Enqueued, JobId = pausedId, At = at, Request = CopyRequest(), Lane = JobLane.Operations, Title = "paused" });
        journal.Append(new JournalRecord { Type = JournalRecordType.StateChanged, JobId = pausedId, At = at, State = JobState.Paused });
        journal.Append(new JournalRecord { Type = JournalRecordType.Enqueued, JobId = doneId, At = at, Request = CopyRequest(), Lane = JobLane.Operations, Title = "done" });
        journal.Append(new JournalRecord { Type = JournalRecordType.StateChanged, JobId = doneId, At = at, State = JobState.Completed });

        var executor = new ScriptedExecutor((_, _) => Task.FromResult(JobOutcome.Completed));
        await using var host = new JobHost(new JobScheduler(), [executor], journal);
        host.Start();

        await WaitUntilAsync(() => host.Scheduler.Find(interruptedId)?.State == JobState.Completed);
        Assert.Equal(JobState.Paused, host.Scheduler.Find(pausedId)!.State);
        Assert.Null(host.Scheduler.Find(doneId));
    }
}
