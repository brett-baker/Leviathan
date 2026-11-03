using Leviathan.Engine.Execution;
using Leviathan.Engine.Supervision;
using Leviathan.Testing;

namespace Leviathan.Engine.Tests;

public class RestartPolicyTests
{
    [Fact]
    public void NormalExit_IsNotRestarted()
    {
        var policy = new RestartPolicy(new ManualTimeProvider());

        Assert.False(policy.OnExit(0, expected: false).Restart);
        Assert.False(policy.OnExit(1, expected: true).Restart);
    }

    [Fact]
    public void ThreeCrashesInFiveMinutes_GivesUp()
    {
        var time = new ManualTimeProvider();
        var policy = new RestartPolicy(time);

        Assert.True(policy.OnExit(-1, false).Restart);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(policy.OnExit(-1, false).Restart);
        time.Advance(TimeSpan.FromMinutes(1));
        var third = policy.OnExit(-1, false);

        Assert.False(third.Restart);
        Assert.True(policy.GaveUp);
        Assert.Contains("3 times", third.Reason);
    }

    [Fact]
    public void CrashesSpreadOut_KeepRestarting()
    {
        var time = new ManualTimeProvider();
        var policy = new RestartPolicy(time);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(policy.OnExit(-1, false).Restart);
            time.Advance(TimeSpan.FromMinutes(3));
        }
    }

    [Fact]
    public void Delay_GrowsWithRecentCrashes()
    {
        var policy = new RestartPolicy(new ManualTimeProvider()) { MaxCrashes = 10 };

        var first = policy.OnExit(-1, false).Delay;
        var second = policy.OnExit(-1, false).Delay;
        var third = policy.OnExit(-1, false).Delay;

        Assert.True(second > first);
        Assert.True(third > second);
    }
}

public class ProcessSupervisorTests
{
    private sealed class FakeProcess(int id) : ISupervisedProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; } = id;

        public Task<int> Exited => _exit.Task;

        public void Exit(int code) => _exit.TrySetResult(code);

        public void Kill() => Exit(-1);

        public void Dispose()
        {
        }
    }

    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<FakeProcess> Started { get; } = [];

        public ISupervisedProcess Start()
        {
            var p = new FakeProcess(Started.Count + 1);
            lock (Started)
            {
                Started.Add(p);
            }

            return p;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Crash_IsRestarted()
    {
        var launcher = new FakeLauncher();
        var policy = new RestartPolicy(TimeProvider.System, new Core.Resilience.BackoffPolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), 0));
        await using var supervisor = new ProcessSupervisor("worker", launcher, policy);

        supervisor.EnsureRunning();
        launcher.Started[0].Exit(-1);

        await WaitUntilAsync(() => launcher.Started.Count == 2 && supervisor.State == SupervisedState.Running);
    }

    [Fact]
    public async Task RestartCount_CountsRestartsAfterCrashes_NotTheFirstStart()
    {
        var launcher = new FakeLauncher();
        var policy = new RestartPolicy(TimeProvider.System, new Core.Resilience.BackoffPolicy(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), 0));
        await using var supervisor = new ProcessSupervisor("worker", launcher, policy);
        Assert.Equal(0, supervisor.RestartCount);
        Assert.Null(supervisor.ProcessId);

        supervisor.EnsureRunning();
        Assert.Equal(0, supervisor.RestartCount);
        Assert.Equal(1, supervisor.ProcessId);

        launcher.Started[0].Exit(-1);
        await WaitUntilAsync(() => launcher.Started.Count == 2 && supervisor.State == SupervisedState.Running);

        Assert.Equal(1, supervisor.RestartCount);
        Assert.Equal(2, supervisor.ProcessId);
    }

    [Fact]
    public async Task RepeatedCrashes_DisableWorker_UntilReenabled()
    {
        var launcher = new FakeLauncher();
        var policy = new RestartPolicy(TimeProvider.System, new Core.Resilience.BackoffPolicy(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), 0));
        await using var supervisor = new ProcessSupervisor("worker", launcher, policy);

        supervisor.EnsureRunning();
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            await WaitUntilAsync(() => launcher.Started.Count == index + 1);
            launcher.Started[index].Exit(-1);
        }

        await WaitUntilAsync(() => supervisor.State == SupervisedState.Disabled);
        Assert.NotNull(supervisor.DisabledReason);

        supervisor.EnsureRunning();
        Assert.Equal(3, launcher.Started.Count);

        supervisor.Reenable();
        Assert.Equal(SupervisedState.Running, supervisor.State);
        Assert.Equal(4, launcher.Started.Count);
    }

    [Fact]
    public async Task ExpectedStop_IsNotRestarted()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new ProcessSupervisor("worker", launcher);

        supervisor.EnsureRunning();
        supervisor.RequestStop();
        launcher.Started[0].Exit(0);

        await WaitUntilAsync(() => supervisor.State == SupervisedState.Stopped);
        Assert.Single(launcher.Started);
    }
}

public class SpeedMeterTests
{
    [Fact]
    public void RecordsOneSamplePerSecond_AndSmoothsSpeed()
    {
        var time = new ManualTimeProvider();
        var meter = new SpeedMeter(time);

        meter.Record(0);
        for (var second = 1; second <= 5; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            meter.Record(second * 100L * 1024 * 1024);
        }

        Assert.Equal(5, meter.History().Count);
        Assert.All(meter.History(), s => Assert.Equal(100.0 * 1024 * 1024, s, 0));
        Assert.Equal(100.0 * 1024 * 1024, meter.BytesPerSecond, 0);
    }

    [Fact]
    public void Stall_ShowsAsZeroSamples()
    {
        var time = new ManualTimeProvider();
        var meter = new SpeedMeter(time);
        meter.Record(0);
        time.Advance(TimeSpan.FromSeconds(1));
        meter.Record(1000);

        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            meter.Record(1000);
        }

        Assert.Equal(new double[] { 1000, 0, 0, 0 }, meter.History());
    }

    [Fact]
    public void History_IsCappedAtTwoMinutes()
    {
        var time = new ManualTimeProvider();
        var meter = new SpeedMeter(time);
        meter.Record(0);
        for (var i = 1; i <= 200; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            meter.Record(i);
        }

        Assert.Equal(SpeedMeter.HistoryLength, meter.History().Count);
    }

    [Fact]
    public void EstimateRemaining_UsesCurrentSpeed()
    {
        var time = new ManualTimeProvider();
        var meter = new SpeedMeter(time);
        Assert.Null(meter.EstimateRemaining(1000));

        meter.Record(0);
        time.Advance(TimeSpan.FromSeconds(1));
        meter.Record(100);

        Assert.Equal(TimeSpan.FromSeconds(9), meter.EstimateRemaining(900));
    }
}
