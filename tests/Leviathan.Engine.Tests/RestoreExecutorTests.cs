using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

public sealed class RestoreExecutorTests
{
    private const string UserBin = @"C:\$Recycle.Bin\S-1-5-21-1";

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 30, 0, TimeSpan.Zero);

    /// <summary>A Windows 10 <c>$I</c> record: version 2, size, deletion time, length of the path in characters, the path.</summary>
    private static byte[] InfoV2(string path, DateTimeOffset deleted)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(2L));
        bytes.AddRange(BitConverter.GetBytes(10L));
        bytes.AddRange(BitConverter.GetBytes(deleted.ToFileTime()));
        bytes.AddRange(BitConverter.GetBytes(path.Length + 1));
        bytes.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
        return [.. bytes];
    }

    /// <summary>A Vista-style <c>$I</c> record: version 1 and a fixed 520-byte path field.</summary>
    private static byte[] InfoV1(string path, DateTimeOffset deleted)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(1L));
        bytes.AddRange(BitConverter.GetBytes(10L));
        bytes.AddRange(BitConverter.GetBytes(deleted.ToFileTime()));
        var field = new byte[520];
        Encoding.Unicode.GetBytes(path).CopyTo(field, 0);
        bytes.AddRange(field);
        return [.. bytes];
    }

    private static void PutInBin(MockFileSystem fs, string id, string original, DateTimeOffset deleted, string content = "x", bool v1 = false)
    {
        var extension = Path.GetExtension(original);
        fs.AddFile($@"{UserBin}\$I{id}{extension}", new MockFileData(v1 ? InfoV1(original, deleted) : InfoV2(original, deleted)));
        fs.AddFile($@"{UserBin}\$R{id}{extension}", new MockFileData(content));
    }

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunAsync(MockFileSystem fs, string[] sources, string[] targets)
    {
        await using var host = new JobHost(new JobScheduler(), [new RestoreExecutor(fs)], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(new JobRequest(JobKind.Restore, sources, null) { Targets = targets }, JobLane.Operations, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(5);
        }

        return (host.Snapshot(host.Scheduler.Find(job.Id)!), host.GetResult(job.Id)!.Items);
    }

    [Fact]
    public void FindRecycled_ReadsAWindows10Record()
    {
        var fs = new MockFileSystem();
        PutInBin(fs, "AAA", @"C:\docs\report.txt", Now);

        var found = RecycleBinIndex.FindRecycled(fs, LocationPath.Parse(@"C:\docs\report.txt"), Now.AddSeconds(-5));

        Assert.Equal($@"{UserBin}\$RAAA.txt", found?.Value);
    }

    [Fact]
    public void FindRecycled_ReadsAVistaRecord_AndIgnoresTheCaseOfThePath()
    {
        var fs = new MockFileSystem();
        PutInBin(fs, "BBB", @"C:\Docs\Report.TXT", Now, v1: true);

        var found = RecycleBinIndex.FindRecycled(fs, LocationPath.Parse(@"C:\docs\report.txt"), Now.AddSeconds(-5));

        Assert.Equal($@"{UserBin}\$RBBB.TXT", found?.Value);
    }

    [Fact]
    public void FindRecycled_PrefersTheNewestEntry_WhenTheSameNameWasDeletedBefore()
    {
        var fs = new MockFileSystem();
        PutInBin(fs, "OLD", @"C:\docs\report.txt", Now.AddDays(-2), content: "old");
        PutInBin(fs, "NEW", @"C:\docs\report.txt", Now, content: "new");

        var found = RecycleBinIndex.FindRecycled(fs, LocationPath.Parse(@"C:\docs\report.txt"), Now.AddDays(-3));

        Assert.Equal("new", fs.File.ReadAllText(found!.Value));
    }

    [Fact]
    public void FindRecycled_IgnoresEntriesFromBeforeThisDelete_SoAnOlderItemIsNeverTakenForIt()
    {
        var fs = new MockFileSystem();
        PutInBin(fs, "OLD", @"C:\docs\report.txt", Now.AddDays(-2));

        Assert.Null(RecycleBinIndex.FindRecycled(fs, LocationPath.Parse(@"C:\docs\report.txt"), Now.AddSeconds(-5)));
    }

    [Fact]
    public void FindRecycled_WithNoBinFolder_ADamagedRecord_OrAShare_FindsNothing()
    {
        var fs = new MockFileSystem();
        Assert.Null(RecycleBinIndex.FindRecycled(fs, LocationPath.Parse(@"C:\docs\report.txt"), Now));

        fs.AddFile($@"{UserBin}\$IBAD.txt", new MockFileData([1, 2, 3]));
        fs.AddFile($@"{UserBin}\$RBAD.txt", new MockFileData("x"));
        Assert.Null(RecycleBinIndex.FindRecycled(fs, LocationPath.Parse(@"C:\docs\report.txt"), Now));
        Assert.Null(RecycleBinIndex.FindRecycled(fs, LocationPath.Parse(@"\\nas\media\a.mkv"), Now));
    }

    [Fact]
    public void FindRecycled_WhenTheDataFileIsMissing_FindsNothing()
    {
        var fs = new MockFileSystem();
        fs.AddFile($@"{UserBin}\$IGONE.txt", new MockFileData(InfoV2(@"C:\docs\report.txt", Now)));

        Assert.Null(RecycleBinIndex.FindRecycled(fs, LocationPath.Parse(@"C:\docs\report.txt"), Now.AddSeconds(-5)));
    }

    [Fact]
    public async Task Restore_MovesFromTheBinBack_AndRemovesTheInfoRecord()
    {
        var fs = new MockFileSystem();
        PutInBin(fs, "AAA", @"C:\docs\report.txt", Now, content: "hello");
        fs.AddDirectory(@"C:\docs");

        var (job, items) = await RunAsync(fs, [$@"{UserBin}\$RAAA.txt"], [@"C:\docs\report.txt"]);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(@"C:\docs\report.txt", items[0].Result);
        Assert.Equal("hello", fs.File.ReadAllText(@"C:\docs\report.txt"));
        Assert.False(fs.File.Exists($@"{UserBin}\$RAAA.txt"));
        Assert.False(fs.File.Exists($@"{UserBin}\$IAAA.txt"));
    }

    [Fact]
    public async Task Restore_RecreatesTheFolderTheItemCameFrom()
    {
        var fs = new MockFileSystem();
        PutInBin(fs, "AAA", @"C:\gone\deep\report.txt", Now);

        var (job, _) = await RunAsync(fs, [$@"{UserBin}\$RAAA.txt"], [@"C:\gone\deep\report.txt"]);

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(fs.File.Exists(@"C:\gone\deep\report.txt"));
    }

    [Fact]
    public async Task Restore_Folder_MovesTheWholeTree()
    {
        var fs = new MockFileSystem();
        fs.AddFile($@"{UserBin}\$RDIR\inner.txt", new MockFileData("i"));
        fs.AddFile($@"{UserBin}\$IDIR", new MockFileData(InfoV2(@"C:\docs\tree", Now)));

        var (job, _) = await RunAsync(fs, [$@"{UserBin}\$RDIR"], [@"C:\docs\tree"]);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("i", fs.File.ReadAllText(@"C:\docs\tree\inner.txt"));
        Assert.False(fs.File.Exists($@"{UserBin}\$IDIR"));
    }

    [Fact]
    public async Task Restore_NeverOverwrites_SoUndoCannotDestroyWhatWasMadeSince()
    {
        var fs = new MockFileSystem();
        PutInBin(fs, "AAA", @"C:\docs\report.txt", Now, content: "old");
        fs.AddFile(@"C:\docs\report.txt", new MockFileData("newer"));

        var (job, items) = await RunAsync(fs, [$@"{UserBin}\$RAAA.txt"], [@"C:\docs\report.txt"]);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(ItemStatus.Failed, items[0].Status);
        Assert.Contains("already exists", items[0].Error, StringComparison.Ordinal);
        Assert.Equal("newer", fs.File.ReadAllText(@"C:\docs\report.txt"));
        Assert.Equal("old", fs.File.ReadAllText($@"{UserBin}\$RAAA.txt"));
    }

    [Fact]
    public async Task Restore_OfAnItemThatIsGone_FailsThatItem_AndRestoresTheOthers()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\docs\b.txt", new MockFileData("b"));

        var (job, items) = await RunAsync(fs, [@"C:\docs\a.txt", @"C:\docs\b.txt"], [@"C:\docs\a-old.txt", @"C:\docs\b-old.txt"]);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal([ItemStatus.Failed, ItemStatus.Done], items.Select(i => i.Status));
        Assert.True(fs.File.Exists(@"C:\docs\b-old.txt"));
    }

    [Fact]
    public async Task Restore_CanChangeOnlyTheCaseOfAName()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\docs\Report.txt", new MockFileData("r"));

        var (job, _) = await RunAsync(fs, [@"C:\docs\Report.txt"], [@"C:\docs\report.txt"]);

        Assert.Equal(JobState.Completed, job.State);
    }

    [Fact]
    public void RestoreRequest_NeedsOneTargetPerSource()
    {
        var one = new JobRequest(JobKind.Restore, [@"C:\a"], null);
        var two = new JobRequest(JobKind.Restore, [@"C:\a"], null) { Targets = [@"C:\b", @"C:\c"] };
        var good = new JobRequest(JobKind.Restore, [@"C:\a"], null) { Targets = [@"C:\b"] };

        Assert.NotNull(JobRequestValidator.Validate(one));
        Assert.NotNull(JobRequestValidator.Validate(two));
        Assert.Null(JobRequestValidator.Validate(good));
    }

    [Fact]
    public async Task DeleteThenRestore_RoundTripsThroughTheBin()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\docs\report.txt", new MockFileData("hello"));
        var bin = new BinThatWritesRecords(fs);
        await using var host = new JobHost(new JobScheduler(), [new DeleteExecutor(fs, bin, new FixedClock()), new RestoreExecutor(fs)], new InMemoryJobJournal());
        host.Start();

        var deleted = host.Submit(new JobRequest(JobKind.Delete, [@"C:\docs\report.txt"], null), JobLane.Operations, null, "delete");
        await WaitAsync(host, deleted.Id);
        var where = host.GetResult(deleted.Id)!.Items[0].Result!;
        Assert.False(fs.File.Exists(@"C:\docs\report.txt"));

        var restored = host.Submit(new JobRequest(JobKind.Restore, [where], null) { Targets = [@"C:\docs\report.txt"] }, JobLane.Operations, null, "restore");
        await WaitAsync(host, restored.Id);

        Assert.Equal("hello", fs.File.ReadAllText(@"C:\docs\report.txt"));
    }

    private static async Task WaitAsync(JobHost host, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Scheduler.Find(id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "The job did not finish.");
            await Task.Delay(5);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Recycles the way Windows does: the item moves to <c>$R…</c> and a <c>$I…</c> record says where it came from.</summary>
    private sealed class BinThatWritesRecords(MockFileSystem fs) : IRecycleBin
    {
        public Task RecycleAsync(LocationPath path, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(path.Value);
            fs.Directory.CreateDirectory(UserBin);
            fs.File.WriteAllBytes($@"{UserBin}\$ITEST{extension}", InfoV2(path.Value, Now));
            fs.File.Move(path.Value, $@"{UserBin}\$RTEST{extension}");
            return Task.CompletedTask;
        }
    }
}
