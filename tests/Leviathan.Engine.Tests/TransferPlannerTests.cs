using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

public sealed class TransferPlannerTests
{
    private sealed class NasResolver : IHostAddressResolver
    {
        public Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(host is "diskstation" or "nas.example.net" ? ["192.168.1.5"] : []);
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static MockFileSystem Disk()
    {
        var fs = new MockFileSystem();
        fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDrive("D:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDirectory(@"C:\dst");
        fs.AddDirectory(@"D:\other");
        fs.AddFile(@"C:\src\tree\a.txt", new MockFileData("hello"));
        fs.AddFile(@"C:\src\tree\sub\b.bin", new MockFileData(new byte[100]));
        fs.AddFile(@"C:\src\one.txt", new MockFileData("12345678"));
        return fs;
    }

    private static TransferPlan Plan(MockFileSystem fs, JobKind kind, string destination, ConflictPolicy conflict, params string[] sources) =>
        new TransferPlanner(fs).Plan(Guid.NewGuid(), new JobRequest(kind, sources, destination) { Conflict = conflict }, CancellationToken.None);

    [Theory]
    [InlineData(@"C:\a\x.mkv", @"C:\b\x.mkv", true, TransferPath.Rename)]
    [InlineData(@"C:\a\x.mkv", @"D:\b\x.mkv", true, TransferPath.ChunkedCopy)]
    [InlineData(@"C:\a\x.mkv", @"C:\b\x.mkv", false, TransferPath.ChunkedCopy)]
    [InlineData(@"\\nas\media\x.mkv", @"\\nas\media\keep\x.mkv", true, TransferPath.Rename)]
    [InlineData(@"\\nas\media\x.mkv", @"\\nas\backup\x.mkv", true, TransferPath.ServerSideCopy)]
    [InlineData(@"\\nas\media\x.mkv", @"\\nas\backup\x.mkv", false, TransferPath.ServerSideCopy)]
    [InlineData(@"\\nas\media\x.mkv", @"\\other\media\x.mkv", false, TransferPath.ChunkedCopy)]
    [InlineData(@"\\nas\media\x.mkv", @"C:\x.mkv", true, TransferPath.ChunkedCopy)]
    [InlineData(@"C:\x.mkv", @"\\nas\media\x.mkv", false, TransferPath.ChunkedCopy)]
    public void Choose_PicksTheFastestPath(string source, string target, bool move, TransferPath expected) =>
        Assert.Equal(expected, new TransferPlanner(new MockFileSystem()).Choose(P(source), P(target), move));

    [Fact]
    public async Task Choose_OneNasUnderTwoNames_IsServerSide_ButNeverARename()
    {
        var identities = new ServerIdentityMap(new NasResolver());
        await identities.IdentifyAsync("diskstation", TimeSpan.FromSeconds(1), default);
        await identities.IdentifyAsync("nas.example.net", TimeSpan.FromSeconds(1), default);
        var planner = new TransferPlanner(new MockFileSystem(), identities);

        // A rename across two spellings of a server is not trusted to work; the NAS copies it itself instead.
        Assert.Equal(TransferPath.ServerSideCopy, planner.Choose(P(@"\\diskstation\media\x.mkv"), P(@"\\nas.example.net\media\x.mkv"), move: true));
        Assert.Equal(TransferPath.ServerSideCopy, planner.Choose(P(@"\\diskstation\media\x.mkv"), P(@"\\nas.example.net\backup\x.mkv"), move: false));
        Assert.Equal(TransferPath.ChunkedCopy, new TransferPlanner(new MockFileSystem()).Choose(P(@"\\diskstation\media\x.mkv"), P(@"\\nas.example.net\media\x.mkv"), move: false));
    }

    [Fact]
    public void Plan_AFolderCopy_ListsEveryFileBeforeAnythingIsCopied_AndTouchesNothing()
    {
        var fs = Disk();
        var before = fs.AllPaths.Count();

        var plan = Plan(fs, JobKind.Copy, @"C:\dst", ConflictPolicy.KeepBoth, @"C:\src\tree", @"C:\src\one.txt");

        Assert.Equal(before, fs.AllPaths.Count());
        Assert.Equal(2, plan.Items.Count);
        var tree = plan.Items[0];
        Assert.Equal(@"C:\dst\tree", tree.Target);
        Assert.Equal(PlanOp.CreateFolder, tree.Steps[0].Op);
        Assert.Equal(2, tree.Steps.Count(s => s.Op == PlanOp.CreateFolder));
        Assert.Equal(2, tree.Steps.Count(s => s.Op == PlanOp.CopyFile));
        Assert.DoesNotContain(tree.Steps, s => s.Op == PlanOp.RemoveFolder);
        Assert.All(plan.Items.SelectMany(i => i.Steps).Where(s => s.Op == PlanOp.CopyFile), s => Assert.Equal(TransferPath.ChunkedCopy, s.Path));
        Assert.Equal(113, plan.BytesTotal);
        Assert.Equal(113, plan.BytesToWrite);
        Assert.Equal(3, plan.FileCount);
    }

    [Fact]
    public void Plan_ASameVolumeMove_IsOneRenamePerItem_AndWritesNothing()
    {
        var plan = Plan(Disk(), JobKind.Move, @"C:\dst", ConflictPolicy.KeepBoth, @"C:\src\tree", @"C:\src\one.txt");

        Assert.All(plan.Items, i => Assert.Equal(PlanOp.RenameItem, Assert.Single(i.Steps).Op));
        Assert.Equal(0, plan.BytesToWrite);
        Assert.Equal(113, plan.BytesTotal);
    }

    [Fact]
    public void Plan_AMoveToAnotherDrive_CopiesEachFile_AndRemovesTheEmptiedFolders()
    {
        var plan = Plan(Disk(), JobKind.Move, @"D:\other", ConflictPolicy.KeepBoth, @"C:\src\tree");

        var ops = plan.Items[0].Steps.Select(s => s.Op).ToList();
        Assert.Equal(PlanOp.CreateFolder, ops[0]);
        Assert.Equal(PlanOp.RemoveFolder, ops[^1]);
        Assert.Equal(2, ops.Count(o => o == PlanOp.RemoveFolder));
        Assert.Equal(105, plan.BytesToWrite);
    }

    [Fact]
    public void Plan_KeepBoth_GivesAClashingFileANumberedName_Inside_AFolder()
    {
        var fs = Disk();
        fs.AddFile(@"C:\dst\tree\a.txt", new MockFileData("old"));

        var plan = Plan(fs, JobKind.Copy, @"C:\dst", ConflictPolicy.Replace, @"C:\src\tree");
        var keep = Plan(fs, JobKind.Copy, @"C:\dst", ConflictPolicy.KeepBoth, @"C:\src\tree");

        Assert.Contains(plan.Items[0].Steps, s => s.Target == @"C:\dst\tree\a.txt");
        Assert.Equal(@"C:\dst\tree (2)", keep.Items[0].Target);
    }

    [Fact]
    public void Plan_Skip_LeavesAnExistingFileOutOfTheTotals()
    {
        var fs = Disk();
        fs.AddFile(@"C:\dst\tree\a.txt", new MockFileData("old"));

        var plan = Plan(fs, JobKind.Copy, @"C:\dst\", ConflictPolicy.Skip, @"C:\src\one.txt", @"C:\src\tree");

        Assert.Equal(ItemStatus.Skipped, plan.Items[1].Status);
        Assert.Empty(plan.Items[1].Steps);
        Assert.Equal(8, plan.BytesTotal);
    }

    [Fact]
    public void Plan_AMissingSource_IsAFailedItem_AndTheRestStillPlan()
    {
        var plan = Plan(Disk(), JobKind.Copy, @"C:\dst", ConflictPolicy.KeepBoth, @"C:\src\gone.txt", @"C:\src\one.txt");

        Assert.Equal(ItemStatus.Failed, plan.Items[0].Status);
        Assert.Contains("no longer exists", plan.Items[0].Note, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, plan.Items[1].Status);
    }

    [Fact]
    public void Plan_MovingIntoTheSameFolder_IsSkipped()
    {
        var plan = Plan(Disk(), JobKind.Move, @"C:\src", ConflictPolicy.KeepBoth, @"C:\src\one.txt");

        Assert.Equal(ItemStatus.Skipped, plan.Items[0].Status);
    }

    [Fact]
    public void Plan_WhenCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new TransferPlanner(Disk()).Plan(Guid.NewGuid(), new JobRequest(JobKind.Copy, [@"C:\src\one.txt"], @"C:\dst"), cts.Token));
    }

    [Fact]
    public void FilePlanStore_SavesAtomically_RoundTrips_AndForgets()
    {
        var fs = Disk();
        var store = new FileTransferPlanStore(fs, @"C:\plans");
        var plan = Plan(fs, JobKind.Move, @"D:\other", ConflictPolicy.Replace, @"C:\src\tree");

        store.Save(plan);
        var loaded = store.TryLoad(plan.JobId);

        Assert.NotNull(loaded);
        Assert.Equal(plan.Items[0].Steps, loaded.Items[0].Steps);
        Assert.Equal(plan.BytesToWrite, loaded.BytesToWrite);
        Assert.Equal(ConflictPolicy.Replace, loaded.Conflict);
        Assert.DoesNotContain(fs.AllFiles, f => f.EndsWith(".tmp", StringComparison.Ordinal));
        store.Delete(plan.JobId);
        Assert.Null(store.TryLoad(plan.JobId));
        store.Delete(plan.JobId);
    }

    [Fact]
    public void FilePlanStore_ADamagedFile_ReadsAsNoPlan()
    {
        var fs = Disk();
        var id = Guid.NewGuid();
        fs.AddFile(@"C:\plans\" + id.ToString("N") + ".plan.json", new MockFileData("{ not json"));

        Assert.Null(new FileTransferPlanStore(fs, @"C:\plans").TryLoad(id));
        Assert.Null(new FileTransferPlanStore(fs, @"C:\plans").TryLoad(Guid.NewGuid()));
    }

    [Fact]
    public async Task Executor_SavesThePlanBeforeCopying_AndForgetsItWhenTheJobEnds()
    {
        var fs = Disk();
        var store = new InMemoryTransferPlanStore();
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, 1024, store)], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\tree"], @"C:\dst"), JobLane.Operations, null, "t");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.Equal(JobState.Completed, host.Scheduler.Find(job.Id)!.State);
        Assert.Null(store.TryLoad(job.Id));
        Assert.True(fs.File.Exists(@"C:\dst\tree\sub\b.bin"));
    }

    [Fact]
    public async Task Executor_NotEnoughSpace_IsKnownFromThePlan_AndForgetsIt()
    {
        var fs = Disk();
        fs.AddDrive("E:", new MockDriveData { TotalSize = 1000, AvailableFreeSpace = 10 });
        fs.AddDirectory(@"E:\tiny");
        var store = new InMemoryTransferPlanStore();
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, 1024, store)], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\tree"], @"E:\tiny"), JobLane.Operations, null, "t");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.Equal(JobState.Failed, host.Scheduler.Find(job.Id)!.State);
        Assert.Null(store.TryLoad(job.Id));
        Assert.False(fs.Directory.Exists(@"E:\tiny\tree"));
    }
}
