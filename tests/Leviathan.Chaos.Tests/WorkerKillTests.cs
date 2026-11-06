using System.Diagnostics;

namespace Leviathan.Chaos.Tests;

/// <summary>
/// Kills the worker stand-in at random points while it copies a tree and starts it again, as a crash, a reboot or a killed process
/// would (spec §3.4, §12.1 step 4). After every kill, nothing may carry a finished name without being a whole copy; after the last
/// run the tree must be complete and no part file may remain. The seed is printed in every failure so a run can be repeated.
/// </summary>
public sealed class WorkerKillTests
{
    private static int Files => int.TryParse(Environment.GetEnvironmentVariable("CHAOS_FILES"), out var n) ? n : 120;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("CHAOS_SEED"), out var n) ? n : Random.Shared.Next();

    [Fact]
    public void KillingTheWorkerAtRandomPoints_NeverLeavesACorruptFile_AndTheCopyStillFinishes()
    {
        var seed = Seed;
        var random = new Random(seed);
        using var tree = new ChaosTree("kill");
        tree.Fill(random, Files, 100_000, 2_000_000);
        var kills = 0;

        for (var round = 0; round < 60; round++)
        {
            using var worker = StartHarness(tree, submit: round == 0);

            // A job counts as accepted once the worker has journaled it; a kill before that loses nothing the user was told about.
            if (round == 0)
            {
                Assert.Equal("SUBMITTED", worker.StandardOutput.ReadLine());
            }

            var allowed = TimeSpan.FromMilliseconds(random.Next(150, 700));
            if (worker.WaitForExit(allowed))
            {
                Assert.True(worker.ExitCode == 0, $"seed {seed}: the worker finished with exit code {worker.ExitCode}");
                break;
            }

            worker.Kill(entireProcessTree: true);
            worker.WaitForExit();
            kills++;
            tree.Inspect();
        }

        // One last run to the end, undisturbed.
        using (var finish = StartHarness(tree, submit: false))
        {
            Assert.True(finish.WaitForExit(TimeSpan.FromSeconds(120)), $"seed {seed}: the last run did not finish");
            Assert.True(finish.ExitCode == 0, $"seed {seed}: the last run ended with exit code {finish.ExitCode}");
        }

        var (missing, parts) = tree.Inspect();
        Assert.True(kills > 0, $"seed {seed}: the copy finished before the first kill; make the tree bigger (CHAOS_FILES)");
        Assert.True(missing.Count == 0, $"seed {seed} after {kills} kills: missing {string.Join(", ", missing.Take(5))}");
        Assert.True(parts.Count == 0, $"seed {seed} after {kills} kills: part files left {string.Join(", ", parts.Take(5))}");
    }

    private static Process StartHarness(ChaosTree tree, bool submit)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Leviathan.Chaos.Harness.dll"));
        info.ArgumentList.Add(tree.State);
        if (submit)
        {
            info.ArgumentList.Add("--submit");
            info.ArgumentList.Add(tree.Source);
            info.ArgumentList.Add(tree.Destination);

            // 25 MB/s keeps a 120-file tree running for a few seconds, so the kills land mid-file.
            info.ArgumentList.Add("25000000");
        }

        return Process.Start(info)!;
    }
}
