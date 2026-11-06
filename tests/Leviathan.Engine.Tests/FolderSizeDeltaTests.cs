using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Engine.Execution;
using Leviathan.Engine.FolderSizes;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;
using Leviathan.Testing;

namespace Leviathan.Engine.Tests;

/// <summary>
/// Stored folder sizes follow Leviathan's own copies, moves, deletes and restores without a rescan (spec §12.7). The real executors
/// run on a mock disk, wrapped the way the worker wraps them.
/// </summary>
public sealed class FolderSizeDeltaTests
{
    private static readonly DateTimeOffset Counted = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MockFileSystem _fs = new();
    private readonly InMemoryFolderSizeStore _store = new();
    private readonly FolderSizeService _service;
    private readonly FolderSizeDeltas _deltas;

    public FolderSizeDeltaTests()
    {
        _service = new FolderSizeService(new LocationProviderRegistry([new FakeLocationProvider()]), _store, new ManualTimeProvider());
        _deltas = new FolderSizeDeltas(_service, _fs);
        _fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });

        // C:\r holds a.bin (10 bytes) and the empty folder sub; C:\dest is empty. All three were counted.
        _fs.AddFile(@"C:\r\a.bin", new MockFileData("0123456789"));
        _fs.AddDirectory(@"C:\r\sub");
        _fs.AddDirectory(@"C:\dest");
        Seed(@"C:\r", ownBytes: 10, ownFiles: 1, ownFolders: 1, totalBytes: 10, totalFiles: 1, totalFolders: 1);
        Seed(@"C:\r\sub", 0, 0, 0, 0, 0, 0);
        Seed(@"C:\dest", 0, 0, 0, 0, 0, 0);
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private void Seed(string path, long ownBytes, int ownFiles, int ownFolders, long totalBytes, int totalFiles, int totalFolders) =>
        _store.Upsert(new FolderSize(P(path), ownBytes, ownFiles, totalBytes, totalFiles, totalFolders, FolderSizeState.Fresh, Counted, null, ownFolders));

    private FolderSize Stored(string path) => _store.Get(P(path))!;

    private async Task RunAsync(IJobExecutor executor, JobRequest request)
    {
        await using var host = new JobHost(new JobScheduler(), [new DeltaTrackingExecutor(executor, _deltas)], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(request, JobLane.Operations, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "the job did not finish");
            await Task.Delay(5);
        }

    }

    [Fact]
    public async Task Copy_AFile_AddsItToTheFolderAndEveryStoredAncestor()
    {
        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Copy, [@"C:\r\a.bin"], @"C:\r\sub"));

        var sub = Stored(@"C:\r\sub");
        Assert.Equal((10, 1, 10, 1), (sub.TotalBytes, sub.TotalFiles, sub.OwnBytes, sub.OwnFiles));
        var root = Stored(@"C:\r");
        Assert.Equal((20, 2), (root.TotalBytes, root.TotalFiles));
        Assert.Equal((10, 1), (root.OwnBytes, root.OwnFiles));
        Assert.Equal(FolderSizeState.Fresh, root.State);
    }

    [Fact]
    public async Task Move_TakesTheItemOutOfOneFolderAndIntoAnother()
    {
        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Move, [@"C:\r\a.bin"], @"C:\dest"));

        var root = Stored(@"C:\r");
        Assert.Equal((0, 0, 0, 0), (root.TotalBytes, root.TotalFiles, root.OwnBytes, root.OwnFiles));
        var dest = Stored(@"C:\dest");
        Assert.Equal((10, 1, 10, 1), (dest.TotalBytes, dest.TotalFiles, dest.OwnBytes, dest.OwnFiles));
    }

    [Fact]
    public async Task Move_WithinOneTree_LeavesTheSharedAncestorsTotalsAlone()
    {
        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Move, [@"C:\r\a.bin"], @"C:\r\sub"));

        var root = Stored(@"C:\r");
        Assert.Equal((10, 1), (root.TotalBytes, root.TotalFiles));
        Assert.Equal((0, 0), (root.OwnBytes, root.OwnFiles));
        Assert.Equal(10, Stored(@"C:\r\sub").TotalBytes);
    }

    [Fact]
    public async Task Delete_TakesTheItemOutOfTheTotals()
    {
        await RunAsync(new DeleteExecutor(_fs, new RemovingBin(_fs), TimeProvider.System), new JobRequest(JobKind.Delete, [@"C:\r\a.bin"], null));

        var root = Stored(@"C:\r");
        Assert.Equal((0, 0, 0, 0), (root.TotalBytes, root.TotalFiles, root.OwnBytes, root.OwnFiles));
    }

    [Fact]
    public async Task Copy_OfACountedFolder_AddsItsWholeSubtree_AndOneFolderToTheParent()
    {
        Seed(@"C:\r\sub", ownBytes: 0, ownFiles: 0, ownFolders: 0, totalBytes: 0, totalFiles: 0, totalFolders: 0);
        _fs.AddFile(@"C:\x\pack\1.bin", new MockFileData("12345"));
        _fs.AddFile(@"C:\x\pack\deep\2.bin", new MockFileData("1234567"));
        Seed(@"C:\x\pack", ownBytes: 5, ownFiles: 1, ownFolders: 1, totalBytes: 12, totalFiles: 2, totalFolders: 1);

        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Copy, [@"C:\x\pack"], @"C:\dest"));

        var dest = Stored(@"C:\dest");
        Assert.Equal((12, 2, 2), (dest.TotalBytes, dest.TotalFiles, dest.TotalFolders));
        Assert.Equal((0, 0, 1), (dest.OwnBytes, dest.OwnFiles, dest.OwnFolders));
    }

    [Fact]
    public async Task Copy_OfAFolderNeverCounted_MarksTheDestinationChainStale_NotWrong()
    {
        _fs.AddFile(@"C:\x\unknown\1.bin", new MockFileData("12345"));

        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Copy, [@"C:\x\unknown"], @"C:\r\sub"));

        Assert.Equal(FolderSizeState.Stale, Stored(@"C:\r\sub").State);
        Assert.Equal(FolderSizeState.Stale, Stored(@"C:\r").State);
        Assert.Equal(10, Stored(@"C:\r").TotalBytes);
    }

    [Fact]
    public async Task Copy_ThatMayReplace_MarksStaleInsteadOfGuessing()
    {
        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Copy, [@"C:\r\a.bin"], @"C:\dest") { Conflict = ConflictPolicy.Replace });

        Assert.Equal(FolderSizeState.Stale, Stored(@"C:\dest").State);
        Assert.Equal(0, Stored(@"C:\dest").TotalBytes);
    }

    [Fact]
    public async Task ADeltaThatChangesFolders_LeavesOwnFoldersUnknownOrExact()
    {
        _fs.AddFile(@"C:\x\pack\1.bin", new MockFileData("12345"));
        Seed(@"C:\x\pack", ownBytes: 5, ownFiles: 1, ownFolders: 0, totalBytes: 5, totalFiles: 1, totalFolders: 0);

        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Copy, [@"C:\x\pack"], @"C:\r\sub"));

        Assert.Equal(1, Stored(@"C:\r\sub").OwnFolders);
        Assert.Equal(1, Stored(@"C:\r\sub").TotalFolders);
        Assert.Equal(2, Stored(@"C:\r").TotalFolders);
    }

    [Fact]
    public async Task AFolderThatMovedAway_TakesItsStoredSubtreeWithIt()
    {
        _fs.AddFile(@"C:\r\sub\1.bin", new MockFileData("1"));
        Seed(@"C:\r\sub", ownBytes: 1, ownFiles: 1, ownFolders: 0, totalBytes: 1, totalFiles: 1, totalFolders: 0);
        Seed(@"C:\r\sub\inner", 0, 0, 0, 0, 0, 0);

        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Move, [@"C:\r\sub"], @"C:\dest"));

        Assert.Null(_store.Get(P(@"C:\r\sub")));
        Assert.Null(_store.Get(P(@"C:\r\sub\inner")));
        Assert.Equal(1, Stored(@"C:\dest").TotalBytes);
    }

    [Fact]
    public async Task AFolderThatWasNeverCounted_IsNotInventedByADelta()
    {
        _fs.AddDirectory(@"C:\elsewhere");

        await RunAsync(new CopyMoveExecutor(_fs), new JobRequest(JobKind.Copy, [@"C:\r\a.bin"], @"C:\elsewhere"));

        Assert.Null(_store.Get(P(@"C:\elsewhere")));
    }

    [Fact]
    public async Task Count_LeavesOutAShareRecycleFolder_SoItAgreesWithADeleteDelta()
    {
        var fake = new FakeLocationProvider()
            .AddFile(@"\\nas\media\a.mkv", 100)
            .AddFile(@"\\nas\media\#recycle\old.mkv", 900)
            .AddFile(@"\\nas\media\.leviathan-trash\batch\x.mkv", 500);
        var service = new FolderSizeService(new LocationProviderRegistry([fake]), new InMemoryFolderSizeStore(), new ManualTimeProvider());

        var size = await service.ComputeAsync(P(@"\\nas\media"), null, CancellationToken.None);

        Assert.Equal(100, size.TotalBytes);
        Assert.Equal(0, size.TotalFolders);
    }

    // A bin that removes the item, like the Windows one does.
    private sealed class RemovingBin(MockFileSystem fs) : Leviathan.Core.Platform.IRecycleBin
    {
        public Task RecycleAsync(LocationPath path, CancellationToken cancellationToken)
        {
            if (fs.Directory.Exists(path.Value))
            {
                fs.Directory.Delete(path.Value, true);
            }
            else
            {
                fs.File.Delete(path.Value);
            }

            return Task.CompletedTask;
        }
    }
}
