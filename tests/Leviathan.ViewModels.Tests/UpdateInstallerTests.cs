using Leviathan.Testing;
using Leviathan.ViewModels.Updates;

namespace Leviathan.ViewModels.Tests;

public sealed class UpdateInstallerTests
{
    private sealed class FakeApplier : IUpdateApplier
    {
        public List<string> Log { get; } = [];

        public Exception? DownloadFails { get; set; }

        public Exception? ApplyFails { get; set; }

        public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
        {
            Log.Add("download");
            progress(30);
            progress(100);
            return DownloadFails is null ? Task.CompletedTask : Task.FromException(DownloadFails);
        }

        public void ApplyAndRestart()
        {
            Log.Add("apply");
            if (ApplyFails is not null)
            {
                throw ApplyFails;
            }
        }
    }

    private sealed class FakeHelpers(List<string> log) : IHelperStopper
    {
        public Task StopAllAsync(CancellationToken cancellationToken)
        {
            log.Add("stop helpers");
            return Task.CompletedTask;
        }
    }

    private static (UpdateInstaller Installer, FakeApplier Applier) Make(Func<JobLoad>? jobs = null, TimeProvider? time = null)
    {
        var applier = new FakeApplier();
        return (new UpdateInstaller(applier, new FakeHelpers(applier.Log), jobs ?? (() => default), time), applier);
    }

    [Fact]
    public async Task Download_ReportsProgress_AndEndsReady()
    {
        var (installer, _) = Make();
        var seen = new List<string>();
        installer.Changed += (_, _) => seen.Add($"{installer.State} {installer.Percent}");

        await installer.DownloadAsync(CancellationToken.None);

        Assert.Equal(InstallState.Ready, installer.State);
        Assert.Contains("Downloading 30", seen);
        Assert.Equal(100, installer.Percent);
    }

    [Fact]
    public async Task Download_AlreadyDownloaded_IsNotRepeated()
    {
        var (installer, applier) = Make();
        await installer.DownloadAsync(CancellationToken.None);

        await installer.DownloadAsync(CancellationToken.None);

        Assert.Single(applier.Log, "download");
    }

    [Fact]
    public async Task Download_WhenItFails_SaysSo_AndCanBeTriedAgain()
    {
        var (installer, applier) = Make();
        applier.DownloadFails = new HttpRequestException("connection reset");
        await installer.DownloadAsync(CancellationToken.None);
        Assert.Equal(InstallState.Failed, installer.State);
        Assert.Contains("connection reset", installer.Message);

        applier.DownloadFails = null;
        await installer.DownloadAsync(CancellationToken.None);

        Assert.Equal(InstallState.Ready, installer.State);
    }

    [Fact]
    public async Task Apply_StopsTheHelpersBeforeHandingOver_InThatOrder()
    {
        var (installer, applier) = Make();
        await installer.DownloadAsync(CancellationToken.None);

        await installer.ApplyAsync(waitForJobs: false, CancellationToken.None);

        Assert.Equal(["download", "stop helpers", "apply"], applier.Log);
        Assert.Equal(InstallState.Applying, installer.State);
    }

    [Fact]
    public async Task Apply_BeforeTheDownload_DownloadsFirst()
    {
        var (installer, applier) = Make();

        await installer.ApplyAsync(waitForJobs: false, CancellationToken.None);

        Assert.Equal(["download", "stop helpers", "apply"], applier.Log);
    }

    [Fact]
    public async Task Apply_AfterAFailedDownload_DoesNotStopAnything()
    {
        var (installer, applier) = Make();
        applier.DownloadFails = new IOException("disk full");

        await installer.ApplyAsync(waitForJobs: false, CancellationToken.None);

        Assert.Equal(["download"], applier.Log);
        Assert.Equal(InstallState.Failed, installer.State);
    }

    [Fact]
    public async Task Apply_WithJobsRunning_AndNoWaiting_StopsAtOnce_TheJobsResumeLater()
    {
        var (installer, applier) = Make(() => new JobLoad(2, 1));
        await installer.DownloadAsync(CancellationToken.None);

        await installer.ApplyAsync(waitForJobs: false, CancellationToken.None);

        Assert.Equal(["download", "stop helpers", "apply"], applier.Log);
    }

    [Fact]
    public async Task Apply_WaitingForJobs_HoldsUntilTheyHaveFinished()
    {
        var time = new ManualTimeProvider();
        var load = new JobLoad(1, 0);
        var (installer, applier) = Make(() => load, time);
        await installer.DownloadAsync(CancellationToken.None);

        var apply = installer.ApplyAsync(waitForJobs: true, CancellationToken.None);
        await Until(() => time.Pending > 0);
        Assert.Equal(InstallState.WaitingForJobs, installer.State);
        Assert.Contains("1 job is running", installer.Message);
        Assert.DoesNotContain("apply", applier.Log);

        load = default;
        time.Advance(UpdateInstaller.JobPoll);
        await apply;

        Assert.Equal(["download", "stop helpers", "apply"], applier.Log);
    }

    [Fact]
    public async Task Apply_WaitingForJobs_CancelledByTheUser_StaysOnTheCurrentVersion()
    {
        var time = new ManualTimeProvider();
        var (installer, applier) = Make(() => new JobLoad(1, 0), time);
        await installer.DownloadAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var apply = installer.ApplyAsync(waitForJobs: true, cts.Token);
        await Until(() => time.Pending > 0);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.Equal(InstallState.Ready, installer.State);
        Assert.DoesNotContain("apply", applier.Log);
        Assert.DoesNotContain("stop helpers", applier.Log);
    }

    [Fact]
    public async Task Apply_WhenTheInstallerCannotStart_FailsAndSaysToRestart()
    {
        var (installer, applier) = Make();
        applier.ApplyFails = new InvalidOperationException("Update.exe is missing");
        await installer.DownloadAsync(CancellationToken.None);

        await installer.ApplyAsync(waitForJobs: false, CancellationToken.None);

        Assert.Equal(InstallState.Failed, installer.State);
        Assert.Contains("Update.exe is missing", installer.Message);
        Assert.Contains("Restart Leviathan", installer.Message);
    }

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, 0, "1 job is running")]
    [InlineData(3, 0, "3 jobs are running")]
    [InlineData(0, 2, "2 jobs are waiting")]
    [InlineData(2, 1, "2 jobs are running and 1 waiting")]
    public void JobLoad_DescribesWhatTheUpdateWouldInterrupt(int running, int queued, string expected) =>
        Assert.Equal(expected, new JobLoad(running, queued).Describe());

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }
}
