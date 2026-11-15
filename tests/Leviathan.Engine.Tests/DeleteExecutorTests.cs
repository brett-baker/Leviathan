using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

public sealed class DeleteExecutorTests
{
    private sealed class FakeBin(MockFileSystem fs) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];

        public bool Refuse { get; set; }

        /// <summary>The network drops on the first call (ERROR_NETNAME_DELETED), as when a mapped share goes away mid-delete.</summary>
        public bool DropOnce { get; set; }

        /// <summary>Leaves the <c>$I</c> / <c>$R</c> pair Windows leaves, with the deletion time of <see cref="FixedTime"/>.</summary>
        public bool WriteRecords { get; set; }

        public Task RecycleAsync(LocationPath path, CancellationToken cancellationToken)
        {
            if (Refuse)
            {
                throw new IOException("This drive has no Recycle Bin.");
            }

            if (DropOnce)
            {
                DropOnce = false;
                throw new IOException("The specified network name is no longer available.", unchecked((int)0x80070040));
            }

            Recycled.Add(path.Value);
            if (WriteRecords)
            {
                var original = path.Value;
                var text = new List<byte>();
                text.AddRange(BitConverter.GetBytes(2L));
                text.AddRange(BitConverter.GetBytes(1L));
                text.AddRange(BitConverter.GetBytes(new DateTimeOffset(2026, 11, 3, 10, 30, 0, TimeSpan.Zero).ToFileTime()));
                text.AddRange(BitConverter.GetBytes(original.Length + 1));
                text.AddRange(System.Text.Encoding.Unicode.GetBytes(original + "\0"));
                fs.AddFile(@"C:\$Recycle.Bin\S-1-5-21-1\$INEW.txt", new MockFileData([.. text]));
                fs.AddFile(@"C:\$Recycle.Bin\S-1-5-21-1\$RNEW.txt", new MockFileData("x"));
            }

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

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 3, 10, 30, 0, TimeSpan.Zero);
    }

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunAsync(MockFileSystem fs, FakeBin bin, params string[] sources) =>
        await RunAsync(fs, bin, DeleteMode.RecycleBin, sources);

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunAsync(MockFileSystem fs, FakeBin bin, DeleteMode mode, params string[] sources) =>
        await RunAsync(fs, bin, mode, keepOwnTrash: true, sources);

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunAsync(MockFileSystem fs, FakeBin bin, DeleteMode mode, bool keepOwnTrash, params string[] sources)
    {
        await using var host = new JobHost(new JobScheduler(), [new DeleteExecutor(fs, bin, new FixedTime())], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(new JobRequest(JobKind.Delete, sources, null) { DeleteMode = mode, KeepOwnTrash = keepOwnTrash }, JobLane.Operations, null, "test");
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

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunOnDrivesAsync(MockFileSystem fs, FakeBin bin, Leviathan.Testing.FakeVolumeInfo volumes, params string[] sources)
    {
        await using var host = new JobHost(new JobScheduler(), [new DeleteExecutor(fs, bin, new FixedTime(), volumes: volumes)], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(new JobRequest(JobKind.Delete, sources, null), JobLane.Operations, null, "test");
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

    private static MockFileSystem Disk()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\a\1.txt", new MockFileData("x"));
        fs.AddFile(@"C:\a\tree\inner.txt", new MockFileData("y"));
        fs.AddFile(@"\\nas\media\show\ep1.mkv", new MockFileData("v"));
        return fs;
    }

    [Fact]
    public async Task LocalDelete_GoesThroughTheRecycleBin()
    {
        var fs = Disk();
        var bin = new FakeBin(fs);

        var (job, items) = await RunAsync(fs, bin, @"C:\a\1.txt", @"C:\a\tree");

        Assert.Equal(JobState.Completed, job.State);
        // Items run side by side, so the order they reach the bin is not fixed; the results keep the order of the request.
        Assert.Equal([@"C:\a\1.txt", @"C:\a\tree"], bin.Recycled.Order(StringComparer.Ordinal));
        Assert.Equal([@"C:\a\1.txt", @"C:\a\tree"], items.Select(i => i.Source));
        Assert.All(items, i => Assert.Equal(ItemStatus.Done, i.Status));
    }

    [Fact]
    public async Task LocalDelete_ReportsWhereTheBinPutIt_SoItCanBeUndone()
    {
        var fs = Disk();
        var bin = new FakeBin(fs) { WriteRecords = true };

        var (_, items) = await RunAsync(fs, bin, @"C:\a\1.txt");

        Assert.Equal(@"C:\$Recycle.Bin\S-1-5-21-1\$RNEW.txt", items[0].Result);
    }

    [Fact]
    public async Task LocalDelete_WhereTheBinKeepsNoRecord_StillSucceeds_ButHasNothingToUndo()
    {
        var fs = Disk();

        var (job, items) = await RunAsync(fs, new FakeBin(fs), @"C:\a\1.txt");

        Assert.Equal(JobState.Completed, job.State);
        Assert.Null(items[0].Result);
    }

    [Fact]
    public async Task LocalDelete_WhenWindowsRefuses_FailsThatItem_AndKeepsIt()
    {
        var fs = Disk();
        var bin = new FakeBin(fs) { Refuse = true };

        var (job, items) = await RunAsync(fs, bin, @"C:\a\1.txt");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no Recycle Bin", items[0].Error, StringComparison.Ordinal);
        Assert.True(fs.File.Exists(@"C:\a\1.txt"));
    }

    [Fact]
    public async Task AShareThatDropsDuringADelete_PutsTheJobInWaiting_AndItFinishesWhenTheShareIsBack()
    {
        var fs = Disk();
        var bin = new FakeBin(fs) { DropOnce = true };
        var retry = new BackoffPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), 0);
        await using var host = new JobHost(new JobScheduler(), [new DeleteExecutor(fs, bin, new FixedTime())], new InMemoryJobJournal(), retryBackoff: retry);
        var sawWaiting = false;
        host.JobChanged += (_, s) => sawWaiting |= s.State == JobState.Waiting;
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.Delete, [@"C:\a\1.txt"], null), JobLane.Operations, null, "delete");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(sawWaiting, "a dropped share must wait and retry, not fail the item");
        Assert.Equal(JobState.Completed, host.Scheduler.Find(job.Id)!.State);
        Assert.Equal([@"C:\a\1.txt"], bin.Recycled);
    }

    [Fact]
    public async Task Permanent_DeletesForGood_IncludingReadOnlyFiles_AndNeverUsesTheBin()
    {
        var fs = Disk();
        fs.File.SetAttributes(@"C:\a\tree\inner.txt", FileAttributes.ReadOnly);
        var bin = new FakeBin(fs);

        var (job, _) = await RunAsync(fs, bin, DeleteMode.Permanent, @"C:\a\1.txt", @"C:\a\tree");

        Assert.Equal(JobState.Completed, job.State);
        Assert.False(fs.File.Exists(@"C:\a\1.txt"));
        Assert.False(fs.Directory.Exists(@"C:\a\tree"));
        Assert.Empty(bin.Recycled);
    }

    [Fact]
    public async Task ShareDelete_IsASoftDelete_IntoAHiddenTrashFolder_WithAManifest()
    {
        var fs = Disk();

        var (job, items) = await RunAsync(fs, new FakeBin(fs), @"\\nas\media\show\ep1.mkv");

        Assert.Equal(JobState.Completed, job.State);
        Assert.False(fs.File.Exists(@"\\nas\media\show\ep1.mkv"));
        var trashed = items[0].Result!;
        Assert.StartsWith(@"\\nas\media\.leviathan-trash\20261103-103000-", trashed, StringComparison.Ordinal);
        Assert.Equal("v", fs.File.ReadAllText(trashed));
        var manifest = fs.File.ReadAllText(@"\\nas\media\.leviathan-trash\manifest.jsonl");
        Assert.Contains(@"\\nas\\media\\show\\ep1.mkv", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareDelete_UsesTheSharesOwnRecycleFolder_WhenThereIsOne_KeepingTheStructure()
    {
        var fs = Disk();
        fs.AddDirectory(@"\\nas\media\#recycle");

        var (_, items) = await RunAsync(fs, new FakeBin(fs), @"\\nas\media\show\ep1.mkv");

        Assert.Equal(@"\\nas\media\#recycle\show\ep1.mkv", items[0].Result);
        Assert.Equal("v", fs.File.ReadAllText(@"\\nas\media\#recycle\show\ep1.mkv"));
    }

    [Fact]
    public async Task ShareDelete_WithOwnTrashOff_AndAVisibleNasBin_UsesTheNasBin_AndWritesNoManifestOrFolderOfOurs()
    {
        var fs = Disk();
        fs.AddDirectory(@"\\nas\media\#recycle");

        var (job, items) = await RunAsync(fs, new FakeBin(fs), DeleteMode.RecycleBin, keepOwnTrash: false, @"\\nas\media\show\ep1.mkv");

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(@"\\nas\media\#recycle\show\ep1.mkv", items[0].Result);
        Assert.False(fs.Directory.Exists(@"\\nas\media\.leviathan-trash"));
    }

    [Fact]
    public async Task ShareDelete_WithOwnTrashOff_AndNoVisibleBin_DeletesOnTheShare_ForTheNasToKeep()
    {
        var fs = Disk();

        var (job, items) = await RunAsync(fs, new FakeBin(fs), DeleteMode.RecycleBin, keepOwnTrash: false, @"\\nas\media\show\ep1.mkv");

        Assert.Equal(JobState.Completed, job.State);
        Assert.False(fs.File.Exists(@"\\nas\media\show\ep1.mkv"));
        Assert.Equal(ItemStatus.Done, items[0].Status);
        Assert.Null(items[0].Result);
        Assert.False(fs.Directory.Exists(@"\\nas\media\.leviathan-trash"));
    }

    [Fact]
    public async Task ShareDelete_WithOwnTrashOff_StillRefusesSomethingAlreadyInABin()
    {
        var fs = Disk();
        fs.AddFile(@"\\nas\media\#recycle\show\old.mkv", new MockFileData("o"));

        var (job, _) = await RunAsync(fs, new FakeBin(fs), DeleteMode.RecycleBin, keepOwnTrash: false, @"\\nas\media\#recycle\show\old.mkv");

        Assert.Equal(JobState.Failed, job.State);
        Assert.True(fs.File.Exists(@"\\nas\media\#recycle\show\old.mkv"));
    }

    [Fact]
    public async Task ShareDelete_OfSomethingAlreadyInABin_IsRefused_NotMovedIntoAnotherBin()
    {
        var fs = Disk();
        fs.AddFile(@"\\nas\media\#recycle\show\old.mkv", new MockFileData("o"));

        var (job, items) = await RunAsync(fs, new FakeBin(fs), @"\\nas\media\#recycle\show\old.mkv");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("already in a recycle folder", items[0].Error, StringComparison.Ordinal);
        Assert.True(fs.File.Exists(@"\\nas\media\#recycle\show\old.mkv"));
    }

    [Fact]
    public async Task ShareDelete_WhereNothingCanBeWrittenTo_FailsThatItem_AndKeepsIt()
    {
        var fs = Disk();
        fs.AddFile(@"\\nas\media\.leviathan-trash", new MockFileData("blocked"));
        fs.AddFile(@"\\nas\media\show\.leviathan-trash", new MockFileData("blocked"));

        var (job, items) = await RunAsync(fs, new FakeBin(fs), @"\\nas\media\show\ep1.mkv");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Shift+Delete", items[0].Error, StringComparison.Ordinal);
        Assert.True(fs.File.Exists(@"\\nas\media\show\ep1.mkv"));
    }

    [Fact]
    public async Task ShareDelete_OfTheSameNameTwice_DoesNotOverwriteWhatIsInTheShareBin()
    {
        var fs = Disk();
        fs.AddFile(@"\\nas\media\#recycle\show\ep1.mkv", new MockFileData("older"));

        var (_, items) = await RunAsync(fs, new FakeBin(fs), @"\\nas\media\show\ep1.mkv");

        Assert.Equal(@"\\nas\media\#recycle\show\ep1 (2).mkv", items[0].Result);
        Assert.Equal("older", fs.File.ReadAllText(@"\\nas\media\#recycle\show\ep1.mkv"));
    }

    [Fact]
    public async Task Delete_OnAFixedDrive_StillUsesTheRecycleBin()
    {
        var fs = Disk();
        var bin = new FakeBin(fs);
        var volumes = new Leviathan.Testing.FakeVolumeInfo().With(@"C:\", "OS", DriveKind.Fixed, 100, 50);

        var (job, _) = await RunOnDrivesAsync(fs, bin, volumes, @"C:\a\1.txt");

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([@"C:\a\1.txt"], bin.Recycled);
    }

    [Fact]
    public async Task Delete_OnAMappedDrive_IsASoftDeleteThroughTheShare_NeverTheWindowsBin()
    {
        var fs = Disk();
        fs.AddFile(@"Y:\show\ep2.mkv", new MockFileData("v2"));
        fs.AddFile(@"\\nas\media\show\ep2.mkv", new MockFileData("v2"));
        var bin = new FakeBin(fs);
        var volumes = new Leviathan.Testing.FakeVolumeInfo().With(@"Y:\", "media", DriveKind.Network, 100, 50, network: @"\\nas\media");

        var (job, items) = await RunOnDrivesAsync(fs, bin, volumes, @"Y:\show\ep2.mkv");

        Assert.Equal(JobState.Completed, job.State);
        Assert.Empty(bin.Recycled);
        Assert.Equal(@"Y:\show\ep2.mkv", items[0].Source);
        Assert.StartsWith(@"\\nas\media\.leviathan-trash\", items[0].Result, StringComparison.Ordinal);
        Assert.False(fs.File.Exists(@"\\nas\media\show\ep2.mkv"));
    }

    [Fact]
    public async Task Delete_OnAMappedDrive_WhoseShareIsUnknown_LeavesTheItemAlone()
    {
        var fs = Disk();
        fs.AddFile(@"Y:\show\ep2.mkv", new MockFileData("v2"));
        var volumes = new Leviathan.Testing.FakeVolumeInfo().With(@"Y:\", "media", DriveKind.Network, 100, 50);

        var (job, items) = await RunOnDrivesAsync(fs, new FakeBin(fs), volumes, @"Y:\show\ep2.mkv");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Shift+Delete", items[0].Error, StringComparison.Ordinal);
        Assert.True(fs.File.Exists(@"Y:\show\ep2.mkv"));
    }

    [Theory]
    [InlineData(DriveKind.Removable)]
    [InlineData(DriveKind.Unknown)]
    public async Task Delete_OnADriveWithNoBin_IsRefused_NotQuietlyMadePermanent(DriveKind kind)
    {
        var fs = Disk();
        fs.AddFile(@"E:\photo.jpg", new MockFileData("p"));
        var bin = new FakeBin(fs);
        var volumes = new Leviathan.Testing.FakeVolumeInfo().With(@"E:\", "USB", kind, 100, 50);

        var (job, items) = await RunOnDrivesAsync(fs, bin, volumes, @"E:\photo.jpg");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no Recycle Bin", items[0].Error, StringComparison.Ordinal);
        Assert.True(fs.File.Exists(@"E:\photo.jpg"));
        Assert.Empty(bin.Recycled);
    }

    [Fact]
    public async Task ADriveRoot_IsRefused_AMissingItemIsAlreadyDone_AndTheOthersStillRun()
    {
        var fs = Disk();
        var bin = new FakeBin(fs);

        var (job, items) = await RunAsync(fs, bin, @"C:\", @"C:\a\gone.txt", @"C:\a\1.txt");

        // A listing can show a file that was deleted a moment ago; that is not a failure (it was a "4 files could not be deleted" message).
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("1 of 3", job.Error, StringComparison.Ordinal);
        Assert.Equal([ItemStatus.Failed, ItemStatus.Skipped, ItemStatus.Done], items.Select(i => i.Status));
        Assert.Equal([@"C:\a\1.txt"], bin.Recycled);
    }

    /// <summary>A Recycle Bin that takes a whole list at once, like the real one, and leaves a record per item as Windows does.</summary>
    private sealed class BatchBin(MockFileSystem fs) : IRecycleBin
    {
        private int _next;

        public List<int> BatchSizes { get; } = [];

        public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task RecycleAsync(LocationPath path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The executor must use the batch call for local items.");

        public Task<IReadOnlyList<Exception?>> RecycleManyAsync(IReadOnlyList<LocationPath> paths, CancellationToken cancellationToken)
        {
            BatchSizes.Add(paths.Count);
            var results = new Exception?[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                if (Refused.Contains(paths[i].Value))
                {
                    results[i] = new IOException("Windows could not move it to the Recycle Bin.");
                    continue;
                }

                var id = $"{++_next:D6}";
                var original = paths[i].Value;
                var record = new List<byte>();
                record.AddRange(BitConverter.GetBytes(2L));
                record.AddRange(BitConverter.GetBytes(1L));
                record.AddRange(BitConverter.GetBytes(new DateTimeOffset(2026, 11, 3, 10, 30, 0, TimeSpan.Zero).ToFileTime()));
                record.AddRange(BitConverter.GetBytes(original.Length + 1));
                record.AddRange(System.Text.Encoding.Unicode.GetBytes(original + "\0"));
                fs.AddFile($@"C:\$Recycle.Bin\S-1-5-21-1\$I{id}.txt", new MockFileData([.. record]));
                fs.AddFile($@"C:\$Recycle.Bin\S-1-5-21-1\$R{id}.txt", new MockFileData("x"));
                fs.File.Delete(original);
            }

            return Task.FromResult<IReadOnlyList<Exception?>>(results);
        }
    }

    [Fact]
    public async Task ManyLocalFiles_GoToTheBinInBatches_EachWithItsOwnPlaceInIt()
    {
        const int count = 450;
        var fs = new MockFileSystem();
        for (var i = 0; i < count; i++)
        {
            fs.AddFile($@"C:\lib\f{i:D4}.txt", new MockFileData("v"));
        }

        var bin = new BatchBin(fs);
        var sources = Enumerable.Range(0, count).Select(i => $@"C:\lib\f{i:D4}.txt").ToArray();

        var (job, items) = await RunWithAsync(new DeleteExecutor(fs, bin, new FixedTime()), sources);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([200, 200, 50], bin.BatchSizes); // a few shell operations, not 450
        Assert.Equal(sources, items.Select(i => i.Source));
        Assert.All(items, i => Assert.Equal(ItemStatus.Done, i.Status));
        Assert.Equal(count, items.Select(i => i.Result).Distinct().Count()); // each knows where it went, so undo works
        Assert.All(items, i => Assert.Matches(@"^C:\\\$Recycle\.Bin\\S-1-5-21-1\\\$R\d{6}\.txt$", i.Result));
    }

    [Fact]
    public async Task WhenWindowsRefusesSomeFilesOfABatch_ThoseFail_AndTheRestAreDone()
    {
        var fs = new MockFileSystem();
        for (var i = 0; i < 10; i++)
        {
            fs.AddFile($@"C:\lib\f{i}.txt", new MockFileData("v"));
        }

        var bin = new BatchBin(fs);
        bin.Refused.Add(@"C:\lib\f3.txt");
        bin.Refused.Add(@"C:\lib\f8.txt");
        var sources = Enumerable.Range(0, 10).Select(i => $@"C:\lib\f{i}.txt").ToArray();

        var (job, items) = await RunWithAsync(new DeleteExecutor(fs, bin, new FixedTime()), sources);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("2 of 10", job.Error, StringComparison.Ordinal);
        Assert.Equal([3, 8], items.Select((item, index) => (item, index)).Where(x => x.item.Status == ItemStatus.Failed).Select(x => x.index));
        Assert.True(fs.File.Exists(@"C:\lib\f3.txt"));
        Assert.False(fs.File.Exists(@"C:\lib\f4.txt"));
    }

    private static MockFileSystem ShareWithFiles(int count)
    {
        var fs = new MockFileSystem();
        for (var i = 0; i < count; i++)
        {
            fs.AddFile($@"\\nas\media\library\file-{i:D5}.mkv", new MockFileData("v"));
        }

        return fs;
    }

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunWithAsync(DeleteExecutor executor, params string[] sources)
    {
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(new JobRequest(JobKind.Delete, sources, null), JobLane.Operations, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(30);
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

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ThousandsOfShareFiles_GoToOneTrashBatch_WithOneLinePerFile_AndResultsInRequestOrder(int parallel)
    {
        const int count = 2_000;
        var fs = ShareWithFiles(count);
        var sources = Enumerable.Range(0, count).Select(i => $@"\\nas\media\library\file-{i:D5}.mkv").ToArray();

        var (job, items) = await RunWithAsync(new DeleteExecutor(fs, new FakeBin(fs), new FixedTime(), maxParallel: parallel), sources);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(sources, items.Select(i => i.Source));
        Assert.All(items, i => Assert.Equal(ItemStatus.Done, i.Status));
        Assert.Empty(fs.Directory.GetFiles(@"\\nas\media\library"));
        var trash = @"\\nas\media\.leviathan-trash";
        Assert.Single(fs.Directory.GetDirectories(trash)); // one batch folder for the job, not one per file
        var lines = fs.File.ReadAllLines(trash + @"\manifest.jsonl");
        Assert.Equal(count, lines.Length);
        Assert.Equal(count, lines.Distinct().Count());
        Assert.Equal(count, items.Select(i => i.Result).Distinct().Count()); // every file has its own place in the trash
        Assert.All(items, i => Assert.True(fs.File.Exists(i.Result!)));
    }

    [Fact]
    public async Task ManyFilesOnAMappedDrive_AskTheDriveOnce()
    {
        const int count = 300;
        var fs = new MockFileSystem();
        for (var i = 0; i < count; i++)
        {
            fs.AddFile($@"Y:\show\f{i:D4}.mkv", new MockFileData("v"));
        }

        fs.AddDirectory(@"\\nas\media\show");
        var volumes = new Leviathan.Testing.FakeVolumeInfo().With(@"Y:\", "media", DriveKind.Network, 100, 50, network: @"\\nas\media");
        var sources = Enumerable.Range(0, count).Select(i => $@"Y:\show\f{i:D4}.mkv").ToArray();

        var (job, items) = await RunWithAsync(new DeleteExecutor(fs, new FakeBin(fs), new FixedTime(), volumes: volumes), sources);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(count, items.Count);
        Assert.Equal(1, volumes.DriveCalls);
    }

    [Fact]
    public async Task AFolderAndItsOwnFile_DeletedTogether_NeverTurnTheJobRed()
    {
        var fs = new MockFileSystem();
        for (var i = 0; i < 50; i++)
        {
            fs.AddFile($@"\\nas\media\tree\sub\file-{i}.txt", new MockFileData("x"));
        }

        var sources = new List<string> { @"\\nas\media\tree" };
        sources.AddRange(Enumerable.Range(0, 50).Select(i => $@"\\nas\media\tree\sub\file-{i}.txt"));

        var (job, items) = await RunWithAsync(new DeleteExecutor(fs, new FakeBin(fs), new FixedTime()), [.. sources]);

        // Whichever item gets there first wins; the other one finds it gone, which is the goal reached, not a failure.
        Assert.Equal(JobState.Completed, job.State);
        Assert.DoesNotContain(items, i => i.Status == ItemStatus.Failed);
        Assert.False(fs.Directory.Exists(@"\\nas\media\tree"));
    }
}
