using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;
using Leviathan.Testing;

namespace Leviathan.Engine.Tests;

public sealed class BandwidthTests
{
    private static DateTime At(int day, int hour, int minute = 0) => new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Local).AddDays(day).AddHours(hour).AddMinutes(minute);

    // 2026-11-02 is a Monday.

    // ---------- windows and rules ----------

    [Fact]
    public void Window_WithinOneDay_IsActiveFromInclusiveToExclusive()
    {
        var window = new BandwidthWindow(TimeSpan.FromHours(9), TimeSpan.FromHours(17), 100);

        Assert.False(window.IsActiveAt(At(0, 8, 59)));
        Assert.True(window.IsActiveAt(At(0, 9)));
        Assert.True(window.IsActiveAt(At(0, 16, 59)));
        Assert.False(window.IsActiveAt(At(0, 17)));
    }

    [Fact]
    public void Window_PastMidnight_RunsIntoTheNextMorning_AndBelongsToTheDayItStarted()
    {
        var night = new BandwidthWindow(TimeSpan.FromHours(22), TimeSpan.FromHours(6), 0) { Days = [DayOfWeek.Friday] };

        Assert.True(night.IsActiveAt(At(4, 23)));
        Assert.True(night.IsActiveAt(At(5, 3)));
        Assert.False(night.IsActiveAt(At(5, 7)));
        Assert.False(night.IsActiveAt(At(5, 23)));
        Assert.False(night.IsActiveAt(At(4, 3)));
    }

    [Fact]
    public void Window_WithNoDays_AppliesEveryDay_AndEqualEndsNeverApply()
    {
        Assert.True(new BandwidthWindow(TimeSpan.FromHours(1), TimeSpan.FromHours(2), 5).IsActiveAt(At(3, 1, 30)));
        Assert.False(new BandwidthWindow(TimeSpan.FromHours(1), TimeSpan.FromHours(1), 5).IsActiveAt(At(3, 1)));
    }

    [Fact]
    public void Rule_UsesTheFirstActiveWindow_ElseTheDefault()
    {
        var rule = new BandwidthRule(10_000_000, [new BandwidthWindow(TimeSpan.FromHours(22), TimeSpan.FromHours(6), 0), new BandwidthWindow(TimeSpan.FromHours(9), TimeSpan.FromHours(17), 1_000_000)]);

        Assert.Equal(10_000_000, rule.LimitAt(At(0, 7)));
        Assert.Equal(1_000_000, rule.LimitAt(At(0, 10)));
        Assert.Equal(0, rule.LimitAt(At(0, 23)));
        Assert.Equal(5, new BandwidthRule(5).LimitAt(At(0, 3)));
    }

    // ---------- the limiter ----------

    [Fact]
    public async Task Limiter_LetsTheFirstChunkGo_AndMakesTheNextWaitItsTurn_WithoutBankingIdleTime()
    {
        var time = new ManualTimeProvider();
        var limiter = new ByteRateLimiter(time);

        await limiter.WaitAsync(1000, 1000, CancellationToken.None);
        var second = limiter.WaitAsync(1000, 1000, CancellationToken.None).AsTask();
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(60));
        await limiter.WaitAsync(1000, 1000, CancellationToken.None);
        var after = limiter.WaitAsync(1000, 1000, CancellationToken.None).AsTask();
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.False(after.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await after.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Limiter_WithNoRate_NeverWaits_AndACancelledWaitEnds()
    {
        var time = new ManualTimeProvider();
        var limiter = new ByteRateLimiter(time);
        await limiter.WaitAsync(1_000_000_000, 0, CancellationToken.None);
        await limiter.WaitAsync(0, 10, CancellationToken.None);
        await limiter.WaitAsync(1000, 1000, CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = limiter.WaitAsync(1000, 1000, cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    // ---------- the controller ----------

    private static BandwidthSettings Settings(long global = 0, long nas = 0) => new()
    {
        Global = new BandwidthRule(global),
        PerDevice = nas > 0 ? new Dictionary<string, BandwidthRule> { [@"\\nas"] = new(nas) } : new Dictionary<string, BandwidthRule>(),
    };

    [Fact]
    public void EffectiveLimit_IsTheSlowestThatApplies_AndZeroMeansNone()
    {
        var controller = new BandwidthController(new ManualTimeProvider()) { Settings = Settings(global: 50, nas: 30) };

        Assert.Equal(30, controller.EffectiveLimit(@"\\nas", 0));
        Assert.Equal(20, controller.EffectiveLimit(@"\\nas", 20));
        Assert.Equal(50, controller.EffectiveLimit(@"D:", 0));
        Assert.Equal(0, new BandwidthController(new ManualTimeProvider()).EffectiveLimit(@"\\nas", 0));
    }

    [Fact]
    public void EffectiveLimit_FollowsTheSchedule()
    {
        var time = new ManualTimeProvider();
        var controller = new BandwidthController(time)
        {
            Settings = new BandwidthSettings { Global = new BandwidthRule(1_000, [new BandwidthWindow(TimeSpan.Zero, TimeSpan.FromHours(23.99), 0)]) },
        };

        Assert.Equal(0, controller.EffectiveLimit(null, 0));
        controller.Settings = Settings(global: 1_000);
        Assert.Equal(1_000, controller.EffectiveLimit(null, 0));
    }

    [Fact]
    public async Task Throttle_PacesAJobToItsLimit()
    {
        var time = new ManualTimeProvider();
        var throttle = new BandwidthController(time) { Settings = Settings(global: 1_000) }.CreateThrottle(null, 0);
        var start = time.GetTimestamp();

        await throttle(1000, CancellationToken.None);
        var second = throttle(1000, CancellationToken.None).AsTask();
        while (!second.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        Assert.InRange(time.GetElapsedTime(start).TotalSeconds, 0.9, 1.3);
    }

    [Fact]
    public async Task Throttle_TwoJobsToOneNas_ShareTheDeviceBudget()
    {
        var time = new ManualTimeProvider();
        var controller = new BandwidthController(time) { Settings = Settings(nas: 1_000) };
        var a = controller.CreateThrottle(@"\\nas", 0);
        var b = controller.CreateThrottle(@"\\nas", 0);

        await a(1000, CancellationToken.None);
        var other = b(1000, CancellationToken.None).AsTask();
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.False(other.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await other.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Throttle_TheJobsOwnLimit_AppliesEvenWithNoOtherLimit_AndAChangedLimitTakesEffectAtOnce()
    {
        var time = new ManualTimeProvider();
        var controller = new BandwidthController(time);
        var throttle = controller.CreateThrottle(null, 500);

        await throttle(1000, CancellationToken.None);
        var waiting = throttle(1000, CancellationToken.None).AsTask();
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var free = controller.CreateThrottle(null, 0);
        await free(1_000_000, CancellationToken.None);
        await free(1_000_000, CancellationToken.None);
    }

    // ---------- end to end ----------

    [Fact]
    public async Task Copy_UnderALimit_TakesTheTimeTheLimitRequires_AndArrivesIntact()
    {
        var time = new ManualTimeProvider();
        var fs = new MockFileSystem();
        fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDrive("D:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDirectory(@"D:\dst");
        var data = new byte[300_000];
        new Random(5).NextBytes(data);
        fs.AddFile(@"C:\src\f.bin", new MockFileData(data));
        var controller = new BandwidthController(time) { Settings = Settings(global: 100_000) };
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, bufferSize: 100_000, bandwidth: controller)], new InMemoryJobJournal());
        host.Start();
        var start = time.GetTimestamp();

        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\f.bin"], @"D:\dst"), JobLane.Operations, null, "copy");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.Equal(JobState.Completed, host.Scheduler.Find(job.Id)!.State);
        Assert.Equal(data, fs.File.ReadAllBytes(@"D:\dst\f.bin"));
        Assert.InRange(time.GetElapsedTime(start).TotalSeconds, 1.9, 2.6);
    }

    // ---------- the background lane yields to a copy on the same NAS ----------

    private sealed class GatedCopy(TaskCompletionSource release) : IJobExecutor
    {
        public bool CanExecute(JobKind kind) => kind == JobKind.Copy;

        public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            await release.Task.WaitAsync(cancellationToken);
            return JobOutcome.Completed;
        }
    }

    private sealed class NoteStart : IJobExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanExecute(JobKind kind) => kind == JobKind.ComputeFolderSize;

        public Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.FromResult(JobOutcome.Completed);
        }
    }

    [Fact]
    public async Task FolderSizes_OnTheNas_WaitWhileACopyToItRuns_ThenGo_ButOtherDevicesAreNotHeldUp()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sizes = new NoteStart();
        await using var host = new JobHost(new JobScheduler(), [new GatedCopy(release), sizes], new InMemoryJobJournal());
        host.Start();

        var copy = host.Submit(new JobRequest(JobKind.Copy, [@"D:\a"], @"\\nas\media"), JobLane.Operations, @"\\nas", "copy");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var onNas = host.Submit(new JobRequest(JobKind.ComputeFolderSize, [@"\\nas\media"], null), JobLane.Background, @"\\nas", "sizes");
        await Task.Delay(150, TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Running, host.Scheduler.Find(copy.Id)!.State);
        Assert.Equal(JobState.Queued, host.Scheduler.Find(onNas.Id)!.State);
        Assert.NotNull(host.Snapshot(host.Scheduler.Find(onNas.Id)!).WaitingReason);
        Assert.False(sizes.Started.Task.IsCompleted);

        release.SetResult();
        await sizes.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
