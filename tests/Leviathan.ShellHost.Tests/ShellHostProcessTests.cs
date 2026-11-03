using System.Diagnostics;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Supervision;

namespace Leviathan.ShellHost.Tests;

/// <summary>
/// The real <c>Leviathan.ShellHost.exe</c> on a private pipe: started on demand, answering on an STA thread, restarted after it is
/// killed, and given up on after repeated crashes (spec §2.2, §3.4). Each test uses its own pipe so none meets a real user's host.
/// </summary>
public sealed class ShellHostProcessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static string HostExe => Path.Combine(AppContext.BaseDirectory, "Leviathan.ShellHost.exe");

    private static RestartPolicy FastPolicy(int maxCrashes = 3) =>
        new(backoff: new BackoffPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), 0)) { MaxCrashes = maxCrashes };

    private static ShellHostConnection Connection(string pipe, RestartPolicy? policy = null) =>
        new(new ExecutableLauncher(HostExe, $"--pipe {pipe}"), pipe, policy ?? FastPolicy());

    private static void KillHost(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    [Fact]
    public async Task TheHost_StartsOnDemand_AnswersPing_AndRunsRequestsOnAnStaThread()
    {
        var pipe = "Leviathan.Tests.ShellHost." + Guid.NewGuid().ToString("N");
        await using var connection = Connection(pipe);
        Assert.Equal(SupervisedState.Stopped, connection.State);

        var service = await connection.GetServiceAsync(Wait, TestContext.Current.CancellationToken);
        var info = await service.PingAsync(TestContext.Current.CancellationToken);
        try
        {
            var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

            Assert.Equal(SupervisedState.Running, connection.State);
            Assert.Equal(Leviathan.Contracts.ContractVersion.Major, info.ContractMajor);
            Assert.Equal("STA", status.Apartment);
            Assert.Equal(2, status.PoolSize);
            Assert.Equal(0, status.AbandonedThreads);
        }
        finally
        {
            KillHost(info.ProcessId);
        }
    }

    [Fact]
    public async Task AfterTheHostIsKilled_TheNextCallReachesARestartedHost()
    {
        var pipe = "Leviathan.Tests.ShellHost." + Guid.NewGuid().ToString("N");
        await using var connection = Connection(pipe);
        var first = await (await connection.GetServiceAsync(Wait, TestContext.Current.CancellationToken)).PingAsync(TestContext.Current.CancellationToken);
        int? secondPid = null;
        try
        {
            KillHost(first.ProcessId);

            // The connection notices the drop; the next call waits for the restarted host instead of failing the menu.
            var deadline = DateTime.UtcNow.Add(Wait);
            while (connection.IsConnected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            var service = await connection.GetServiceAsync(Wait, TestContext.Current.CancellationToken);
            var second = await service.PingAsync(TestContext.Current.CancellationToken);
            secondPid = second.ProcessId;

            Assert.NotEqual(first.ProcessId, second.ProcessId);
            Assert.Equal("STA", (await service.GetStatusAsync(TestContext.Current.CancellationToken)).Apartment);
        }
        finally
        {
            KillHost(first.ProcessId);
            if (secondPid is { } pid)
            {
                KillHost(pid);
            }
        }
    }

    [Fact]
    public async Task AfterRepeatedCrashes_TheHostIsDisabled_AndCallsFailAtOnceWithAReason()
    {
        var pipe = "Leviathan.Tests.ShellHost." + Guid.NewGuid().ToString("N");
        await using var connection = Connection(pipe, FastPolicy(maxCrashes: 2));
        var pids = new List<int>();
        try
        {
            for (var crash = 0; crash < 2; crash++)
            {
                var service = await connection.GetServiceAsync(Wait, TestContext.Current.CancellationToken);
                var info = await service.PingAsync(TestContext.Current.CancellationToken);
                pids.Add(info.ProcessId);
                KillHost(info.ProcessId);
                var deadline = DateTime.UtcNow.Add(Wait);
                while (connection.IsConnected && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }
            }

            var disabledBy = DateTime.UtcNow.Add(Wait);
            while (connection.State != SupervisedState.Disabled && DateTime.UtcNow < disabledBy)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            var ex = await Assert.ThrowsAsync<ShellHostUnavailableException>(() => connection.GetServiceAsync(Wait, TestContext.Current.CancellationToken));

            Assert.Equal(SupervisedState.Disabled, connection.State);
            Assert.Contains("turned off", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            pids.ForEach(KillHost);
        }
    }
}
