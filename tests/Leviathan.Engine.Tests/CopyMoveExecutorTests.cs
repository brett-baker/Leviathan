using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

public sealed class CopyMoveExecutorTests
{
    private static MockFileSystem Disk()
    {
        var fs = new MockFileSystem();
        fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDrive("D:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDirectory(@"C:\src");
        fs.AddDirectory(@"C:\dst");
        fs.AddDirectory(@"D:\other");
        fs.AddFile(@"C:\src\a.txt", new MockFileData("hello") { LastWriteTime = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero) });
        fs.AddFile(@"C:\src\b.txt", new MockFileData("bee"));
        return fs;
    }

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunAsync(MockFileSystem fs, JobRequest request, int buffer = 1024)
    {
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, buffer)], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(request, JobLane.Operations, null, "test");
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

    private static JobRequest Copy(string destination, ConflictPolicy conflict = ConflictPolicy.KeepBoth, params string[] sources) =>
        new(JobKind.Copy, sources, destination) { Conflict = conflict };

    private static JobRequest Move(string destination, ConflictPolicy conflict = ConflictPolicy.KeepBoth, params string[] sources) =>
        new(JobKind.Move, sources, destination) { Conflict = conflict };

    [Fact]
    public async Task Copy_PutsTheFileInPlace_KeepsTheOriginal_AndTheTimestamp_AndLeavesNoPartFile()
    {
        var fs = Disk();

        var (job, items) = await RunAsync(fs, Copy(@"D:\other", sources: @"C:\src\a.txt"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("hello", fs.File.ReadAllText(@"D:\other\a.txt"));
        Assert.True(fs.File.Exists(@"C:\src\a.txt"));
        Assert.Equal(fs.File.GetLastWriteTimeUtc(@"C:\src\a.txt"), fs.File.GetLastWriteTimeUtc(@"D:\other\a.txt"));
        Assert.DoesNotContain(fs.AllFiles, f => f.EndsWith(CopyMoveExecutor.PartSuffix, StringComparison.Ordinal));
        var item = Assert.Single(items);
        Assert.Equal(ItemStatus.Done, item.Status);
        Assert.Equal(@"D:\other\a.txt", item.Result);
        Assert.Equal(5, job.BytesDone);
    }

    [Fact]
    public async Task Copy_OfSeveralChunks_IsExact()
    {
        var fs = Disk();
        var data = new byte[10_000];
        new Random(1).NextBytes(data);
        fs.AddFile(@"C:\src\big.bin", new MockFileData(data));

        await RunAsync(fs, Copy(@"D:\other", sources: @"C:\src\big.bin"), buffer: 777);

        Assert.Equal(data, fs.File.ReadAllBytes(@"D:\other\big.bin"));
    }

    [Fact]
    public async Task Copy_OfAFolder_CopiesTheWholeTree()
    {
        var fs = Disk();
        fs.AddFile(@"C:\src\tree\one.txt", new MockFileData("1"));
        fs.AddFile(@"C:\src\tree\deep\two.txt", new MockFileData("22"));
        fs.AddDirectory(@"C:\src\tree\empty");

        var (job, _) = await RunAsync(fs, Copy(@"D:\other", sources: @"C:\src\tree"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("22", fs.File.ReadAllText(@"D:\other\tree\deep\two.txt"));
        Assert.True(fs.Directory.Exists(@"D:\other\tree\empty"));
        Assert.True(fs.File.Exists(@"C:\src\tree\one.txt"));
    }

    [Fact]
    public async Task Conflict_KeepBoth_AddsANumber_AndNeverOverwrites()
    {
        var fs = Disk();
        fs.AddFile(@"D:\other\a.txt", new MockFileData("old"));

        var (_, items) = await RunAsync(fs, Copy(@"D:\other", ConflictPolicy.KeepBoth, @"C:\src\a.txt"));

        Assert.Equal("old", fs.File.ReadAllText(@"D:\other\a.txt"));
        Assert.Equal("hello", fs.File.ReadAllText(@"D:\other\a (2).txt"));
        Assert.Equal(@"D:\other\a (2).txt", items[0].Result);
    }

    [Fact]
    public async Task Conflict_Replace_Overwrites()
    {
        var fs = Disk();
        fs.AddFile(@"D:\other\a.txt", new MockFileData("old"));

        await RunAsync(fs, Copy(@"D:\other", ConflictPolicy.Replace, @"C:\src\a.txt"));

        Assert.Equal("hello", fs.File.ReadAllText(@"D:\other\a.txt"));
    }

    [Fact]
    public async Task Conflict_Skip_LeavesTheExistingFile_AndReportsSkipped()
    {
        var fs = Disk();
        fs.AddFile(@"D:\other\a.txt", new MockFileData("old"));

        var (job, items) = await RunAsync(fs, Copy(@"D:\other", ConflictPolicy.Skip, @"C:\src\a.txt"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("old", fs.File.ReadAllText(@"D:\other\a.txt"));
        Assert.Equal(ItemStatus.Skipped, items[0].Status);
    }

    [Fact]
    public async Task Conflict_ReplaceMergesFolders_AndKeepsFilesNotInTheSource()
    {
        var fs = Disk();
        fs.AddFile(@"C:\src\tree\new.txt", new MockFileData("n"));
        fs.AddFile(@"D:\other\tree\keep.txt", new MockFileData("k"));

        await RunAsync(fs, Copy(@"D:\other", ConflictPolicy.Replace, @"C:\src\tree"));

        Assert.True(fs.File.Exists(@"D:\other\tree\keep.txt"));
        Assert.True(fs.File.Exists(@"D:\other\tree\new.txt"));
    }

    [Fact]
    public async Task Copy_IntoItsOwnFolder_MakesANumberedCopy()
    {
        var fs = Disk();

        await RunAsync(fs, Copy(@"C:\src", ConflictPolicy.Replace, @"C:\src\a.txt"));

        Assert.Equal("hello", fs.File.ReadAllText(@"C:\src\a (2).txt"));
        Assert.Equal("hello", fs.File.ReadAllText(@"C:\src\a.txt"));
    }

    [Fact]
    public async Task Move_OnTheSameVolume_IsARename()
    {
        var fs = Disk();

        var (job, items) = await RunAsync(fs, Move(@"C:\dst", sources: @"C:\src\a.txt"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.False(fs.File.Exists(@"C:\src\a.txt"));
        Assert.Equal("hello", fs.File.ReadAllText(@"C:\dst\a.txt"));
        Assert.Equal(@"C:\dst\a.txt", items[0].Result);
    }

    [Fact]
    public async Task Move_ToAnotherVolume_CopiesVerifiesAndThenRemovesTheSource()
    {
        var fs = Disk();
        fs.AddFile(@"C:\src\tree\one.txt", new MockFileData("1"));
        fs.AddFile(@"C:\src\tree\deep\two.txt", new MockFileData("22"));

        var (job, _) = await RunAsync(fs, Move(@"D:\other", sources: [@"C:\src\a.txt", @"C:\src\tree"]));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("hello", fs.File.ReadAllText(@"D:\other\a.txt"));
        Assert.Equal("22", fs.File.ReadAllText(@"D:\other\tree\deep\two.txt"));
        Assert.False(fs.File.Exists(@"C:\src\a.txt"));
        Assert.False(fs.Directory.Exists(@"C:\src\tree"));
    }

    [Fact]
    public async Task Move_IntoTheFolderItIsAlreadyIn_ChangesNothing()
    {
        var fs = Disk();

        var (job, items) = await RunAsync(fs, Move(@"C:\src", sources: @"C:\src\a.txt"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(ItemStatus.Skipped, items[0].Status);
        Assert.Equal("hello", fs.File.ReadAllText(@"C:\src\a.txt"));
    }

    [Fact]
    public async Task AMissingSource_FailsThatItemOnly_AndTheRestStillRun()
    {
        var fs = Disk();

        var (job, items) = await RunAsync(fs, Copy(@"D:\other", sources: [@"C:\src\gone.txt", @"C:\src\b.txt"]));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("1 of 2", job.Error, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Failed, items[0].Status);
        Assert.Equal(ItemStatus.Done, items[1].Status);
        Assert.Equal("bee", fs.File.ReadAllText(@"D:\other\b.txt"));
    }

    [Fact]
    public async Task AMissingDestination_FailsTheJobBeforeTouchingAnything()
    {
        var fs = Disk();

        var (job, items) = await RunAsync(fs, Copy(@"D:\nowhere", sources: @"C:\src\a.txt"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("does not exist", job.Error, StringComparison.Ordinal);
        Assert.Empty(items);
        Assert.True(fs.File.Exists(@"C:\src\a.txt"));
    }

    [Fact]
    public async Task NotEnoughSpace_FailsUpFront_AndTouchesNothing()
    {
        var fs = Disk();
        fs.AddDrive("E:", new MockDriveData { TotalSize = 100, AvailableFreeSpace = 2 });
        fs.AddDirectory(@"E:\tiny");

        var (job, items) = await RunAsync(fs, Copy(@"E:\tiny", sources: @"C:\src\a.txt"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Not enough space", job.Error, StringComparison.Ordinal);
        Assert.Empty(items);
        Assert.Empty(fs.Directory.GetFiles(@"E:\tiny"));
    }

    [Fact]
    public async Task APartFileFromAnEarlierCrash_IsOverwritten_AndNeverMistakenForTheResult()
    {
        var fs = Disk();
        fs.AddFile(@"D:\other\a.txt" + CopyMoveExecutor.PartSuffix, new MockFileData("garbage from a crash"));

        await RunAsync(fs, Copy(@"D:\other", sources: @"C:\src\a.txt"));

        Assert.Equal("hello", fs.File.ReadAllText(@"D:\other\a.txt"));
        Assert.DoesNotContain(fs.AllFiles, f => f.EndsWith(CopyMoveExecutor.PartSuffix, StringComparison.Ordinal));
    }
}
