using Leviathan.Contracts;
using Leviathan.Engine.Scheduling;
using Leviathan.Testing;

namespace Leviathan.Engine.Tests;

public class JobSchedulerTests
{
    private static JobRequest Copy(JobPriority priority = JobPriority.Normal, bool paused = false) =>
        new(JobKind.Copy, [@"D:\a"], @"\\nas\media") { Priority = priority, StartPaused = paused };

    private static JobScheduler Create(int maxRunning = 3, int perDevice = 1, int background = 2) =>
        new(new ConcurrencyLimits(maxRunning, perDevice, background), new ManualTimeProvider());

    [Fact]
    public void TakeRunnable_RespectsPerDeviceLimit()
    {
        var scheduler = Create();
        var first = scheduler.Enqueue(Copy(), JobLane.Operations, @"\\nas", "one");
        var second = scheduler.Enqueue(Copy(), JobLane.Operations, @"\\nas", "two");

        var started = scheduler.TakeRunnable();

        Assert.Equal(new[] { first.Id }, started.Select(j => j.Id));
        Assert.Equal(JobState.Queued, second.State);
        Assert.Contains("1 job per device", second.WaitingReason);
    }

    [Fact]
    public void TakeRunnable_RespectsGlobalLimit_AcrossDevices()
    {
        var scheduler = Create(maxRunning: 2, perDevice: 4);
        scheduler.Enqueue(Copy(), JobLane.Operations, "C:", "a");
        scheduler.Enqueue(Copy(), JobLane.Operations, "D:", "b");
        var third = scheduler.Enqueue(Copy(), JobLane.Operations, "E:", "c");

        Assert.Equal(2, scheduler.TakeRunnable().Count);
        Assert.Equal(JobState.Queued, third.State);
        Assert.Equal("Waiting: 2 jobs running", third.WaitingReason);
    }

    [Fact]
    public void MarkFinished_FreesSlot_ForNextJob()
    {
        var scheduler = Create();
        var first = scheduler.Enqueue(Copy(), JobLane.Operations, @"\\nas", "one");
        var second = scheduler.Enqueue(Copy(), JobLane.Operations, @"\\nas", "two");
        scheduler.TakeRunnable();

        scheduler.MarkFinished(first.Id, JobState.Completed);
        var started = scheduler.TakeRunnable();

        Assert.Equal(new[] { second.Id }, started.Select(j => j.Id));
        Assert.NotNull(first.FinishedAt);
    }

    [Fact]
    public void Background_DoesNotUseOperationSlots_AndYieldsToOperationsOnSameDevice()
    {
        var scheduler = Create(maxRunning: 1);
        var copy = scheduler.Enqueue(Copy(), JobLane.Operations, @"\\nas", "copy");
        var sizesOnNas = scheduler.Enqueue(new JobRequest(JobKind.ComputeFolderSize, [@"\\nas\media"], null), JobLane.Background, @"\\nas", "sizes nas");
        var sizesOnC = scheduler.Enqueue(new JobRequest(JobKind.ComputeFolderSize, [@"C:\"], null), JobLane.Background, "C:", "sizes c");

        var started = scheduler.TakeRunnable().Select(j => j.Id).ToList();

        Assert.Contains(copy.Id, started);
        Assert.Contains(sizesOnC.Id, started);
        Assert.DoesNotContain(sizesOnNas.Id, started);
        Assert.Equal("Waiting: a job is using this device", sizesOnNas.WaitingReason);
        Assert.Equal(-1, sizesOnC.ColorIndex);
    }

    [Fact]
    public void Background_LaneHasItsOwnLimit()
    {
        var scheduler = Create(background: 1);
        scheduler.Enqueue(new JobRequest(JobKind.ComputeFolderSize, [@"C:\a"], null), JobLane.Background, "C:", "a");
        var second = scheduler.Enqueue(new JobRequest(JobKind.ComputeFolderSize, [@"D:\b"], null), JobLane.Background, "D:", "b");

        Assert.Single(scheduler.TakeRunnable());
        Assert.Equal("Waiting: background lane busy", second.WaitingReason);
    }

    [Fact]
    public void Enqueue_HigherPriority_GoesAheadOfNormal()
    {
        var scheduler = Create(maxRunning: 1, perDevice: 1);
        var normal = scheduler.Enqueue(Copy(), JobLane.Operations, null, "normal");
        var high = scheduler.Enqueue(Copy(JobPriority.High), JobLane.Operations, null, "high");

        Assert.Equal(0, scheduler.QueuePosition(high.Id));
        Assert.Equal(1, scheduler.QueuePosition(normal.Id));
        Assert.Equal(high.Id, scheduler.TakeRunnable().Single().Id);
    }

    [Fact]
    public void Move_ChangesOrder()
    {
        var scheduler = Create(maxRunning: 1);
        var a = scheduler.Enqueue(Copy(), JobLane.Operations, null, "a");
        var b = scheduler.Enqueue(Copy(), JobLane.Operations, null, "b");
        var c = scheduler.Enqueue(Copy(), JobLane.Operations, null, "c");

        Assert.True(scheduler.Move(c.Id, 0));

        Assert.Equal(new[] { c.Id, a.Id, b.Id }, scheduler.Snapshot().Select(j => j.Id));
        Assert.True(scheduler.Move(c.Id, 99));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, scheduler.Snapshot().Select(j => j.Id));
    }

    [Fact]
    public void RunNow_StartsAboveTheLimit()
    {
        var scheduler = Create(maxRunning: 1);
        scheduler.Enqueue(Copy(), JobLane.Operations, @"\\nas", "a");
        var urgent = scheduler.Enqueue(Copy(), JobLane.Operations, @"\\nas", "urgent");
        scheduler.TakeRunnable();

        Assert.True(scheduler.RunNow(urgent.Id));
        var started = scheduler.TakeRunnable();

        Assert.Equal(new[] { urgent.Id }, started.Select(j => j.Id));
        Assert.True(urgent.Forced);
    }

    [Fact]
    public void PausedJobs_DoNotStart_UntilResumed()
    {
        var scheduler = Create();
        var job = scheduler.Enqueue(Copy(paused: true), JobLane.Operations, null, "staged");

        Assert.Empty(scheduler.TakeRunnable());
        Assert.Equal(JobState.Paused, job.State);

        Assert.True(scheduler.Resume(job.Id));
        Assert.Single(scheduler.TakeRunnable());
    }

    [Fact]
    public void Cancel_QueuedJob_FinishesImmediately_RunningJobWaitsForExecutor()
    {
        var scheduler = Create(maxRunning: 1);
        var running = scheduler.Enqueue(Copy(), JobLane.Operations, null, "running");
        var queued = scheduler.Enqueue(Copy(), JobLane.Operations, null, "queued");
        scheduler.TakeRunnable();

        Assert.Equal(JobState.Queued, scheduler.Cancel(queued.Id));
        Assert.Equal(JobState.Cancelled, queued.State);

        Assert.Equal(JobState.Running, scheduler.Cancel(running.Id));
        Assert.Equal(JobState.Running, running.State);
        Assert.Null(scheduler.Cancel(queued.Id));
    }

    [Fact]
    public void Remove_RefusesRunningJobs()
    {
        var scheduler = Create();
        var job = scheduler.Enqueue(Copy(), JobLane.Operations, null, "a");
        scheduler.TakeRunnable();

        Assert.False(scheduler.Remove(job.Id));
        scheduler.MarkFinished(job.Id, JobState.Completed);
        Assert.True(scheduler.Remove(job.Id));
        Assert.Empty(scheduler.Snapshot());
    }

    [Fact]
    public void Colors_AreDistinctForUpToEightActiveJobs_AndReusedAfterFinish()
    {
        var scheduler = Create(maxRunning: 16, perDevice: 16);
        var jobs = Enumerable.Range(0, 8).Select(i => scheduler.Enqueue(Copy(), JobLane.Operations, null, $"j{i}")).ToList();

        Assert.Equal(8, jobs.Select(j => j.ColorIndex).Distinct().Count());
        Assert.All(jobs, j => Assert.InRange(j.ColorIndex, 0, JobScheduler.PaletteSize - 1));

        scheduler.Cancel(jobs[3].Id);
        var next = scheduler.Enqueue(Copy(), JobLane.Operations, null, "next");

        Assert.Equal(jobs[3].ColorIndex, next.ColorIndex);
    }

    [Fact]
    public void MarkWaiting_ThenRequeue_RestartsJob()
    {
        var scheduler = Create();
        var job = scheduler.Enqueue(Copy(), JobLane.Operations, @"\\nas", "a");
        scheduler.TakeRunnable();

        scheduler.MarkWaiting(job.Id, "Waiting for nas");
        Assert.Equal(JobState.Waiting, job.State);
        Assert.Empty(scheduler.TakeRunnable());

        scheduler.Requeue(job.Id);
        Assert.Single(scheduler.TakeRunnable());
    }

    [Fact]
    public void SetLimits_TakesEffectOnNextPass()
    {
        var scheduler = Create(maxRunning: 1, perDevice: 4);
        scheduler.Enqueue(Copy(), JobLane.Operations, null, "a");
        scheduler.Enqueue(Copy(), JobLane.Operations, null, "b");
        Assert.Single(scheduler.TakeRunnable());

        scheduler.SetLimits(new ConcurrencyLimits(2, 4, 2));

        Assert.Single(scheduler.TakeRunnable());
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.SetLimits(new ConcurrencyLimits(0, 1, 1)));
    }

    [Fact]
    public void PruneFinished_RemovesOldFinishedJobsOnly()
    {
        var time = new ManualTimeProvider();
        var scheduler = new JobScheduler(new ConcurrencyLimits(), time);
        var old = scheduler.Enqueue(Copy(), JobLane.Operations, null, "old");
        var pending = scheduler.Enqueue(Copy(), JobLane.Operations, "X:", "pending", null);
        scheduler.Cancel(old.Id);
        time.Advance(TimeSpan.FromDays(8));

        Assert.Equal(1, scheduler.PruneFinished(TimeSpan.FromDays(7)));
        Assert.Equal(new[] { pending.Id }, scheduler.Snapshot().Select(j => j.Id));
    }

    [Fact]
    public void Changed_IsRaisedForEveryChange()
    {
        var scheduler = Create();
        var changes = new List<SchedulerChange>();
        scheduler.Changed += (_, c) => changes.Add(c);

        var job = scheduler.Enqueue(Copy(), JobLane.Operations, null, "a");
        scheduler.Pause(job.Id);
        scheduler.Remove(job.Id);

        Assert.Equal(3, changes.Count);
        Assert.True(changes[^1].Removed);
    }

    [Fact]
    public void RandomOperations_NeverExceedLimits()
    {
        // Property-style check with a fixed seed: whatever sequence of operations runs, the limits hold.
        var random = new Random(20261101);
        var limits = new ConcurrencyLimits(3, 1, 2);
        var scheduler = new JobScheduler(limits, new ManualTimeProvider());
        string[] devices = [@"\\nas", "C:", "D:", @"\\backup"];
        var ids = new List<Guid>();

        for (var step = 0; step < 2000; step++)
        {
            switch (random.Next(7))
            {
                case 0 or 1:
                    var lane = random.Next(4) == 0 ? JobLane.Background : JobLane.Operations;
                    ids.Add(scheduler.Enqueue(Copy((JobPriority)random.Next(3)), lane, devices[random.Next(devices.Length)], $"j{step}").Id);
                    break;
                case 2:
                    scheduler.TakeRunnable();
                    break;
                case 3 when ids.Count > 0:
                    var running = scheduler.Snapshot().Where(j => j.State == JobState.Running).ToList();
                    if (running.Count > 0)
                    {
                        scheduler.MarkFinished(running[random.Next(running.Count)].Id, JobState.Completed);
                    }

                    break;
                case 4 when ids.Count > 0:
                    scheduler.Pause(ids[random.Next(ids.Count)]);
                    break;
                case 5 when ids.Count > 0:
                    scheduler.Resume(ids[random.Next(ids.Count)]);
                    break;
                case 6 when ids.Count > 0:
                    scheduler.Move(ids[random.Next(ids.Count)], random.Next(10));
                    break;
            }

            var runningNow = scheduler.Snapshot().Where(j => j.State == JobState.Running).ToList();
            Assert.True(runningNow.Count(j => j.Lane == JobLane.Operations) <= limits.MaxRunningJobs, $"Global limit exceeded at step {step}");
            Assert.True(runningNow.Count(j => j.Lane == JobLane.Background) <= limits.MaxBackgroundJobs, $"Background limit exceeded at step {step}");
            foreach (var group in runningNow.Where(j => j.Lane == JobLane.Operations).GroupBy(j => j.DeviceKey))
            {
                Assert.True(group.Count() <= limits.MaxPerDevice, $"Device limit exceeded for {group.Key} at step {step}");
            }
        }
    }
}
