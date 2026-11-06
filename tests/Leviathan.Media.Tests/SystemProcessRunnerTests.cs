using System.Diagnostics;

namespace Leviathan.Media.Tests;

/// <summary>The real process runner with ordinary Windows programs: output, exit codes, the output cap, and the kill on a timeout.</summary>
public sealed class SystemProcessRunnerTests
{
    private static readonly SystemProcessRunner Runner = new();

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "These tests start cmd.exe and ping.exe.");

    [Fact]
    public async Task Run_CollectsOutput_AndTheExitCode()
    {
        RequireWindows();

        var outcome = await Runner.RunAsync("cmd.exe", ["/c", "echo hello & exit 3"], TimeSpan.FromSeconds(20), 1024, CancellationToken.None);

        Assert.Equal(3, outcome.ExitCode);
        Assert.Contains("hello", outcome.Output);
        Assert.False(outcome.TimedOut);
        Assert.False(outcome.OutputTruncated);
    }

    [Fact]
    public async Task Run_WhenItWritesMoreThanTheCap_KeepsTheStart_AndSaysTruncated()
    {
        RequireWindows();

        var outcome = await Runner.RunAsync("cmd.exe", ["/c", "for /l %i in (1,1,2000) do @echo line %i"], TimeSpan.FromSeconds(30), 1000, CancellationToken.None);

        Assert.True(outcome.OutputTruncated);
        Assert.True(outcome.Output.Length <= 1000 + 8192);
        Assert.StartsWith("line 1", outcome.Output);
    }

    [Fact]
    public async Task Run_WhenItOutlivesTheTimeout_IsKilled_PromptlyAndWithItsChildren()
    {
        RequireWindows();
        var clock = Stopwatch.StartNew();

        var outcome = await Runner.RunAsync("cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromMilliseconds(600), 1024, CancellationToken.None);

        Assert.True(outcome.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        await Task.Delay(300);
        Assert.DoesNotContain(Process.GetProcessesByName("PING"), p => p.StartTime > DateTime.Now.AddSeconds(-20) && !p.HasExited);
    }

    [Fact]
    public async Task Run_WhenCancelled_Throws_NotReportsATimeout()
    {
        RequireWindows();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner.RunAsync("cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromSeconds(30), 1024, cts.Token));
    }

    [Fact]
    public async Task Run_OfAProgramThatDoesNotExist_Throws_ForTheCallerToReportAMissingTool()
    {
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => Runner.RunAsync(Path.Combine(Path.GetTempPath(), "no-such-program-" + Guid.NewGuid().ToString("N") + ".exe"), [], TimeSpan.FromSeconds(5), 1024, CancellationToken.None));
    }
}
