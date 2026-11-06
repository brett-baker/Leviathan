using System.Diagnostics;
using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;
using Leviathan.Integration.Tests.Lab;

namespace Leviathan.Chaos.Tests;

/// <summary>
/// Pulls the cable in the middle of a real copy to a real SMB share (spec §3.3, §12.2): the job must go to Waiting, not fail, and
/// finish by itself once the line is back, with every file whole and no part file left. The share is the Samba container from
/// <c>docker/compose.yml</c>, reached through Toxiproxy with the Windows SMB client on a non-standard port (<c>net use /TCPPORT</c>);
/// never a real share. Skipped, with the reason, when the lab or that Windows feature is not available.
/// </summary>
public sealed class NetworkDropTests(NetworkLab lab) : IClassFixture<NetworkLab>
{
    private const string Server = @"\\127.0.0.1\leviathan";

    [Fact]
    public async Task CableDropMidCopy_PutsTheJobInWaiting_AndItFinishesWholeWhenTheLineIsBack()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows SMB client is needed.");
        lab.Require();
        await lab.ResetAsync();

        // A connection left over from an earlier run would keep its old port.
        await NetUseAsync("use", Server, "/delete", "/y");
        var mapped = await NetUseAsync("use", Server, $"/TCPPORT:{NetworkLab.ProxyPort}", "/user:lab", "lab-only-test-password", "/persistent:no");
        Assert.SkipUnless(mapped, "Windows could not connect to the lab share through the proxy (net use /TCPPORT).");

        var target = Path.Combine(Server, "chaos-" + Guid.NewGuid().ToString("N"));
        using var tree = new ChaosTree("netdrop");
        try
        {
            Directory.CreateDirectory(target);
            tree.Fill(new Random(7), 24, 400_000, 1_500_000);
            var fs = new FileSystem();
            var executor = new CopyMoveExecutor(
                fs,
                planStore: new FileTransferPlanStore(fs, Path.Combine(tree.State, "plans")),
                journal: new FileTransferJournal(fs, Path.Combine(tree.State, "progress")),
                bandwidth: new BandwidthController());
            await using var host = new JobHost(
                new JobScheduler(),
                [executor],
                new InMemoryJobJournal(),
                retryBackoff: new BackoffPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), 0));
            host.Start();

            // 4 MB/s keeps the copy running for several seconds, so the line is cut while a file is half written.
            var job = host.Submit(
                new JobRequest(JobKind.Copy, [tree.Source], target) { Verify = VerifyMode.Full, BytesPerSecondLimit = 4_000_000 },
                JobLane.Operations,
                null,
                "network drop copy");

            await UntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Running, TimeSpan.FromSeconds(20), "the job to start");
            await Task.Delay(1500);
            await lab.CutAsync();

            await UntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Waiting, TimeSpan.FromSeconds(90), "the job to wait for the share");
            Assert.NotEqual(JobState.Failed, host.Scheduler.Find(job.Id)!.State);

            await lab.RestoreAsync();
            await UntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Completed, TimeSpan.FromSeconds(180), "the job to finish after the line came back");

            var copied = Path.Combine(target, "tree");
            foreach (var file in Directory.EnumerateFiles(tree.Source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(tree.Source, file);
                Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(copied, relative))), $"{relative} differs");
            }

            Assert.Empty(Directory.EnumerateFiles(copied, "*" + CopyMoveExecutor.PartSuffix, SearchOption.AllDirectories));
        }
        finally
        {
            await lab.RestoreAsync();
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (IOException)
            {
                // The lab is thrown away with `down -v` anyway.
            }

            await NetUseAsync("use", Server, "/delete", "/y");
        }
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan limit, string what)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    private static async Task<bool> NetUseAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo("net") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }
}
