using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Chaos.Tests;

/// <summary>
/// A disk that fills up and a file that refuses to be written, in the middle of a real copy on a real disk (spec §12.1, §3.3). The
/// copier is the portable one with a fault injected for chosen files; everything else is the real executor and job host. One bad
/// file must not stop the job, take a good file with it, or leave anything with a finished name that is not a whole copy.
/// </summary>
public sealed class DiskFaultTests
{
    private const int ErrorDiskFull = unchecked((int)0x80070070);

    private sealed class FaultyCopier(IFileCopier inner, Func<string, Exception?> fault) : IFileCopier
    {
        public async Task<FileCopyResult> CopyAsync(FileCopyRequest request, CancellationToken cancellationToken)
        {
            if (fault(request.Source) is not { } problem)
            {
                return await inner.CopyAsync(request, cancellationToken).ConfigureAwait(false);
            }

            // What the real copier does when the disk fills halfway: some bytes reach the part file, then the write fails and the part is removed.
            await File.WriteAllBytesAsync(request.Target + CopyMoveExecutor.PartSuffix, new byte[10_000], cancellationToken).ConfigureAwait(false);
            File.Delete(request.Target + CopyMoveExecutor.PartSuffix);
            throw problem;
        }
    }

    private static async Task<(JobSnapshot Job, JobResult? Result)> RunAsync(ChaosTree tree, Func<string, Exception?> fault)
    {
        var fs = new FileSystem();
        var executor = new CopyMoveExecutor(
            fs,
            planStore: new FileTransferPlanStore(fs, Path.Combine(tree.State, "plans")),
            journal: new FileTransferJournal(fs, Path.Combine(tree.State, "progress")),
            copier: new FaultyCopier(new ChunkedFileCopier(fs, () => BufferPolicy.Fixed(64 * 1024)), fault));
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(
            new JobRequest(JobKind.Copy, [tree.Source], tree.Destination) { OnError = ErrorMode.SkipAndReport, Verify = VerifyMode.Full },
            JobLane.Operations,
            null,
            "fault copy");

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return (host.Snapshot(host.Scheduler.Find(job.Id)!), host.GetResult(job.Id));
    }

    [Fact]
    public async Task DiskFullOnOneFile_SkipsThatFile_CopiesTheRest_AndLeavesNoPartFile()
    {
        using var tree = new ChaosTree("diskfull");
        tree.Fill(new Random(11), 30, 20_000, 400_000);
        var victim = Path.Combine(tree.Source, "d3", "sub 0", "file 00003 ✓.bin");

        var (job, _) = await RunAsync(tree, source => source == victim ? new IOException("There is not enough space on the disk.", ErrorDiskFull) : null);

        Assert.True(job.State is JobState.Completed or JobState.Failed, $"the job did not finish: {job.State}");
        var (missing, parts) = tree.Inspect();
        Assert.Empty(parts);
        Assert.Equal([Path.GetRelativePath(tree.Source, victim)], missing);
    }

    [Fact]
    public async Task AccessDeniedOnSeveralFiles_SkipsThem_AndTheOthersArriveWhole()
    {
        using var tree = new ChaosTree("denied");
        tree.Fill(new Random(12), 30, 20_000, 400_000);
        var denied = new HashSet<string>(Enumerable.Range(0, 30).Where(i => i % 7 == 0)
            .Select(i => Path.Combine(tree.Source, "d" + (i % 7), "sub " + (i % 3), $"file {i:D5} ✓.bin")));

        var (job, _) = await RunAsync(tree, source => denied.Contains(source) ? new UnauthorizedAccessException("Access is denied.") : null);

        Assert.True(job.State is JobState.Completed or JobState.Failed, $"the job did not finish: {job.State}");
        var (missing, parts) = tree.Inspect();
        Assert.Empty(parts);
        Assert.Equal(denied.Select(d => Path.GetRelativePath(tree.Source, d)).Order(StringComparer.Ordinal), missing.Order(StringComparer.Ordinal));
    }
}
