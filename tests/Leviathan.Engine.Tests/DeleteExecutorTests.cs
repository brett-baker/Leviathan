using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
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

        public Task RecycleAsync(LocationPath path, CancellationToken cancellationToken)
        {
            if (Refuse)
            {
                throw new IOException("This drive has no Recycle Bin.");
            }

            Recycled.Add(path.Value);
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

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunAsync(MockFileSystem fs, FakeBin bin, DeleteMode mode, params string[] sources)
    {
        await using var host = new JobHost(new JobScheduler(), [new DeleteExecutor(fs, bin, new FixedTime())], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(new JobRequest(JobKind.Delete, sources, null) { DeleteMode = mode }, JobLane.Operations, null, "test");
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
        Assert.Equal([@"C:\a\1.txt", @"C:\a\tree"], bin.Recycled);
        Assert.All(items, i => Assert.Equal(ItemStatus.Done, i.Status));
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
    public async Task ShareDelete_OfTheSameNameTwice_DoesNotOverwriteWhatIsInTheShareBin()
    {
        var fs = Disk();
        fs.AddFile(@"\\nas\media\#recycle\show\ep1.mkv", new MockFileData("older"));

        var (_, items) = await RunAsync(fs, new FakeBin(fs), @"\\nas\media\show\ep1.mkv");

        Assert.Equal(@"\\nas\media\#recycle\show\ep1 (2).mkv", items[0].Result);
        Assert.Equal("older", fs.File.ReadAllText(@"\\nas\media\#recycle\show\ep1.mkv"));
    }

    [Fact]
    public async Task ADriveRoot_AndAMissingItem_AreRefused_WhileTheOthersStillRun()
    {
        var fs = Disk();
        var bin = new FakeBin(fs);

        var (job, items) = await RunAsync(fs, bin, @"C:\", @"C:\a\gone.txt", @"C:\a\1.txt");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("2 of 3", job.Error, StringComparison.Ordinal);
        Assert.Equal([ItemStatus.Failed, ItemStatus.Failed, ItemStatus.Done], items.Select(i => i.Status));
        Assert.Equal([@"C:\a\1.txt"], bin.Recycled);
    }
}
