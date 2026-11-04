using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using Leviathan.Contracts;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

public sealed class ResumeTests
{
    private static byte[] Pattern(int length, int seed = 0)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 31) + (i >> 8) + seed);
        }

        return data;
    }

    private static FileCopyRequest Request(long resumeFrom = 0, Action<long>? onBytes = null, Action<long>? onConfirmed = null, bool hash = false) =>
        new(@"C:\src\f.bin", @"C:\dst\f.bin", TransferPath.ChunkedCopy, hash, onBytes ?? (_ => { }), resumeFrom, onConfirmed);

    private static MockFileSystem Disk(int length)
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"C:\dst");
        fs.AddFile(@"C:\src\f.bin", new MockFileData(Pattern(length)));
        return fs;
    }

    // ---------- copier ----------

    [Fact]
    public async Task Copier_ResumesFromTheConfirmedBytes_AndTheResultIsExact_WithTheWholeFileHashed()
    {
        var fs = Disk(2_000_000);
        // A part file that has the first 700,000 bytes right and garbage after (what a crash can leave).
        var part = Pattern(1_500_000);
        Array.Fill(part, (byte)0xEE, 700_000, 800_000);
        fs.AddFile(@"C:\dst\f.bin.leviathan-part", new MockFileData(part));
        long counted = 0;

        var result = await new ChunkedFileCopier(fs, () => BufferPolicy.Fixed(100_000))
            .CopyAsync(Request(resumeFrom: 700_000, onBytes: n => counted += n, hash: true), CancellationToken.None);

        Assert.Equal(Pattern(2_000_000), fs.File.ReadAllBytes(@"C:\dst\f.bin"));
        Assert.Equal(2_000_000, counted);
        Assert.Equal(SHA256.HashData(Pattern(2_000_000)), result.SourceHash);
        Assert.False(fs.File.Exists(@"C:\dst\f.bin.leviathan-part"));
    }

    [Fact]
    public async Task Copier_ResumeBeyondThePartFile_ContinuesFromWhatIsThere()
    {
        var fs = Disk(500_000);
        fs.AddFile(@"C:\dst\f.bin.leviathan-part", new MockFileData(Pattern(100_000)));

        await new ChunkedFileCopier(fs).CopyAsync(Request(resumeFrom: 400_000), CancellationToken.None);

        Assert.Equal(Pattern(500_000), fs.File.ReadAllBytes(@"C:\dst\f.bin"));
    }

    [Fact]
    public async Task Copier_ResumeWithoutAPartFile_StartsOver()
    {
        var fs = Disk(300_000);
        long counted = 0;

        await new ChunkedFileCopier(fs).CopyAsync(Request(resumeFrom: 200_000, onBytes: n => counted += n), CancellationToken.None);

        Assert.Equal(Pattern(300_000), fs.File.ReadAllBytes(@"C:\dst\f.bin"));
        Assert.Equal(300_000, counted);
    }

    [Fact]
    public async Task Copier_UnbufferedResume_RoundsDownToASector()
    {
        var fs = Disk(100_000);
        fs.AddFile(@"C:\dst\f.bin.leviathan-part", new MockFileData(Pattern(50_000)));

        await new ChunkedFileCopier(fs, () => BufferPolicy.Fixed(8192), unbufferedThreshold: 1).CopyAsync(Request(resumeFrom: 50_000), CancellationToken.None);

        Assert.Equal(Pattern(100_000), fs.File.ReadAllBytes(@"C:\dst\f.bin"));
    }

    [Fact]
    public async Task Copier_ReportsConfirmedOffsets_AsTheFileIsWritten()
    {
        var fs = Disk(1_000_000);
        var confirmed = new List<long>();

        await new ChunkedFileCopier(fs, () => BufferPolicy.Fixed(100_000), confirmEveryBytes: 250_000)
            .CopyAsync(Request(onConfirmed: confirmed.Add), CancellationToken.None);

        Assert.NotEmpty(confirmed);
        Assert.Equal(confirmed.Order().ToList(), confirmed);
        Assert.All(confirmed, c => Assert.InRange(c, 1, 1_000_000));
    }

    [Fact]
    public async Task Copier_WhenCancelled_KeepsThePartFileForAResume_ButNeverTheTarget()
    {
        var fs = Disk(2_000_000);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ChunkedFileCopier(fs, () => BufferPolicy.Fixed(100_000)).CopyAsync(Request(onBytes: _ => cts.Cancel()), cts.Token));

        Assert.False(fs.File.Exists(@"C:\dst\f.bin"));
        Assert.True(fs.File.Exists(@"C:\dst\f.bin.leviathan-part"));
    }

    // ---------- journal ----------

    [Fact]
    public void Journal_ReplaysWhatWasRecorded_AndIgnoresATornLastLine()
    {
        var fs = new MockFileSystem();
        var id = Guid.NewGuid();
        using (var journal = new FileTransferJournal(fs, @"C:\j"))
        {
            journal.StepDone(id, 0, 1);
            journal.FileConfirmed(id, 0, 2, 4096);
            journal.FileConfirmed(id, 0, 2, 8192);
            journal.ItemFinished(id, 1, ItemStatus.Failed, "denied");
        }

        var path = @"C:\j\" + id.ToString("N") + ".progress";
        fs.File.AppendAllText(path, "{\"T\":\"s\",\"I\":5,\"S\":");
        var progress = new FileTransferJournal(fs, @"C:\j").Load(id);

        Assert.Equal([(0, 1)], progress.DoneSteps);
        Assert.Equal(8192, progress.Confirmed[(0, 2)]);
        Assert.Equal((ItemStatus.Failed, "denied"), progress.Items[1]);
        Assert.DoesNotContain((5, 0), progress.DoneSteps);
    }

    [Fact]
    public void Journal_SkipsGarbageLines_AndAMissingJournalIsEmpty()
    {
        var fs = new MockFileSystem();
        var id = Guid.NewGuid();
        fs.AddFile(@"C:\j\" + id.ToString("N") + ".progress", new MockFileData("not json\n{\"T\":\"s\",\"I\":2,\"S\":3,\"B\":0}\n\n"));

        var journal = new FileTransferJournal(fs, @"C:\j");

        Assert.Equal([(2, 3)], journal.Load(id).DoneSteps);
        Assert.Empty(journal.Load(Guid.NewGuid()).DoneSteps);
    }

    [Fact]
    public void Journal_AppendsAfterALoad_AndDeleteForgetsEverything()
    {
        var fs = new MockFileSystem();
        var id = Guid.NewGuid();
        var journal = new FileTransferJournal(fs, @"C:\j");
        journal.StepDone(id, 0, 0);
        _ = journal.Load(id);
        journal.StepDone(id, 0, 1);

        Assert.Equal(2, journal.Load(id).DoneSteps.Count);
        journal.Delete(id);
        Assert.Empty(journal.Load(id).DoneSteps);
        journal.Delete(id);
        journal.Dispose();
    }

    [Fact]
    public void InMemoryJournal_BehavesLikeTheFileOne()
    {
        var journal = new InMemoryTransferJournal();
        var id = Guid.NewGuid();
        journal.StepDone(id, 1, 2);
        journal.FileConfirmed(id, 1, 3, 10);
        journal.ItemFinished(id, 1, ItemStatus.Done, null);

        var copy = journal.Load(id);

        Assert.Contains((1, 2), copy.DoneSteps);
        Assert.Equal(10, copy.Confirmed[(1, 3)]);
        Assert.Equal(ItemStatus.Done, copy.Items[1].Status);
        journal.Delete(id);
        Assert.Empty(journal.Load(id).DoneSteps);
    }

    // ---------- executor ----------

    /// <summary>Copies like the real copier, except that one named file drops the network once, half way through.</summary>
    private sealed class DroppingCopier(IFileSystem fs, string dropFile, bool dropForever = false) : IFileCopier
    {
        private readonly ChunkedFileCopier _real = new(fs, () => BufferPolicy.Fixed(50_000), confirmEveryBytes: 50_000);
        private bool _dropped;

        public List<(string Source, long ResumeFrom)> Calls { get; } = [];

        public async Task<FileCopyResult> CopyAsync(FileCopyRequest request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Source, request.ResumeFrom));
            if (request.Source.EndsWith(dropFile, StringComparison.Ordinal) && (dropForever || !_dropped))
            {
                _dropped = true;
                var half = fs.FileInfo.New(request.Source).Length / 2 / 50_000 * 50_000;
                var part = request.Target + CopyMoveExecutor.PartSuffix;
                fs.File.WriteAllBytes(part, fs.File.ReadAllBytes(request.Source)[..(int)half]);
                request.OnConfirmed?.Invoke(half);
                throw new IOException("The specified network name is no longer available.", unchecked((int)0x80070040));
            }

            return await _real.CopyAsync(request, cancellationToken);
        }
    }

    private static MockFileSystem Tree()
    {
        var fs = new MockFileSystem();
        fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDrive("D:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDirectory(@"D:\dst");
        fs.AddFile(@"C:\src\tree\a.bin", new MockFileData(Pattern(120_000, 1)));
        fs.AddFile(@"C:\src\tree\b.bin", new MockFileData(Pattern(300_000, 2)));
        fs.AddFile(@"C:\src\tree\sub\c.bin", new MockFileData(Pattern(80_000, 3)));
        return fs;
    }

    private static async Task<JobState> WaitAsync(JobHost host, Guid id, Func<JobState, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var state = host.Scheduler.Find(id)!.State;
            if (done(state))
            {
                return state;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException();
    }

    [Fact]
    public async Task Move_ThatLosesTheNetworkMidFile_WaitsAndThenContinuesFromTheConfirmedBytes()
    {
        var fs = Tree();
        var copier = new DroppingCopier(fs, "b.bin");
        var executor = new CopyMoveExecutor(fs, planStore: new InMemoryTransferPlanStore(), copier: copier);
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal(), retryBackoff: new BackoffPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), 0));
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.Move, [@"C:\src\tree"], @"D:\dst"), JobLane.Operations, null, "move");
        var state = await WaitAsync(host, job.Id, s => s is JobState.Completed or JobState.Failed);

        Assert.Equal(JobState.Completed, state);
        Assert.Equal(Pattern(300_000, 2), fs.File.ReadAllBytes(@"D:\dst\tree\b.bin"));
        Assert.Equal(Pattern(120_000, 1), fs.File.ReadAllBytes(@"D:\dst\tree\a.bin"));
        Assert.Equal(Pattern(80_000, 3), fs.File.ReadAllBytes(@"D:\dst\tree\sub\c.bin"));
        Assert.False(fs.Directory.Exists(@"C:\src\tree"));
        // b.bin was started twice, the second time from the 150,000 bytes that were safe; a.bin and c.bin were not copied again.
        Assert.Equal(2, copier.Calls.Count(c => c.Source.EndsWith("b.bin", StringComparison.Ordinal)));
        Assert.Equal(150_000, copier.Calls.Last(c => c.Source.EndsWith("b.bin", StringComparison.Ordinal)).ResumeFrom);
        Assert.Single(copier.Calls, c => c.Source.EndsWith("a.bin", StringComparison.Ordinal));
        Assert.Single(copier.Calls, c => c.Source.EndsWith("c.bin", StringComparison.Ordinal));
        Assert.Single(host.GetResult(job.Id)!.Items);
    }

    [Fact]
    public async Task Move_InterruptedByAWorkerRestart_ContinuesTheSamePlan_InANewProcess()
    {
        var fs = Tree();
        var plans = new InMemoryTransferPlanStore();
        var progress = new InMemoryTransferJournal();
        var jobJournal = new InMemoryJobJournal();
        Guid id;
        await using (var first = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, planStore: plans, copier: new DroppingCopier(fs, "b.bin", dropForever: true), journal: progress)], jobJournal,
            retryBackoff: new BackoffPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 0)))
        {
            first.Start();
            id = first.Submit(new JobRequest(JobKind.Move, [@"C:\src\tree"], @"D:\dst"), JobLane.Operations, null, "move").Id;
            await WaitAsync(first, id, s => s == JobState.Waiting);
        }

        // "Reboot": a new host reads the job journal; the plan and progress journal are what the first process left.
        await using var second = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, planStore: plans, journal: progress)], jobJournal);
        second.Start();
        var state = await WaitAsync(second, id, s => s is JobState.Completed or JobState.Failed);

        Assert.Equal(JobState.Completed, state);
        Assert.Equal(Pattern(300_000, 2), fs.File.ReadAllBytes(@"D:\dst\tree\b.bin"));
        Assert.Equal(Pattern(120_000, 1), fs.File.ReadAllBytes(@"D:\dst\tree\a.bin"));
        Assert.Null(plans.TryLoad(id));
        Assert.Empty(progress.Load(id).DoneSteps);
    }

    [Fact]
    public async Task KeepBoth_NamesChosenBeforeTheInterruption_AreTheSameAfterIt()
    {
        var fs = Tree();
        fs.AddFile(@"D:\dst\tree\a.bin", new MockFileData("old"));
        var plans = new InMemoryTransferPlanStore();
        var copier = new DroppingCopier(fs, "b.bin");
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, planStore: plans, copier: copier)], new InMemoryJobJournal(),
            retryBackoff: new BackoffPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), 0));
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\tree"], @"D:\dst") { Conflict = ConflictPolicy.KeepBoth }, JobLane.Operations, null, "copy");
        await WaitAsync(host, job.Id, s => s is JobState.Completed or JobState.Failed);

        // The folder clashed, so everything went into "tree (2)" - once, not into a "tree (2)" and then a "tree (3)".
        Assert.True(fs.Directory.Exists(@"D:\dst\tree (2)"));
        Assert.False(fs.Directory.Exists(@"D:\dst\tree (3)"));
        Assert.Equal(Pattern(300_000, 2), fs.File.ReadAllBytes(@"D:\dst\tree (2)\b.bin"));
        Assert.Equal("old", fs.File.ReadAllText(@"D:\dst\tree\a.bin"));
    }

    [Fact]
    public async Task APlanForADifferentRequest_IsNotReused()
    {
        var fs = Tree();
        var plans = new InMemoryTransferPlanStore();
        var progress = new InMemoryTransferJournal();
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, planStore: plans, journal: progress)], new InMemoryJobJournal());
        var request = new JobRequest(JobKind.Copy, [@"C:\src\tree\a.bin"], @"D:\dst");
        var job = host.Submit(request, JobLane.Operations, null, "copy");
        // A stale plan with the job's id but for other sources, as a reused id or an edited request would leave.
        plans.Save(new TransferPlan(job.Id, false, ConflictPolicy.KeepBoth, @"D:\dst", [new PlanItem(@"C:\src\other.bin", @"D:\dst\other.bin", ItemStatus.Done, null, 1, [])]));
        progress.StepDone(job.Id, 0, 0);
        host.Start();

        await WaitAsync(host, job.Id, s => s is JobState.Completed or JobState.Failed);

        Assert.Equal(Pattern(120_000, 1), fs.File.ReadAllBytes(@"D:\dst\a.bin"));
    }
}
