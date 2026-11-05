using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

/// <summary>The Soft-delete bin's engine (spec §12.5): registry, manifest, scan, retention, purge and restore.</summary>
public sealed class SoftDeleteBinTests
{
    private const string Root = @"\\nas\media\.leviathan-trash";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static LocationPath P(string text) => LocationPath.Parse(text);

    private static string Line(string original, string trashed, DateTimeOffset at) =>
        System.Text.Json.JsonSerializer.Serialize(new { original, trashed, at }) + "\n";

    private static (MockFileSystem Fs, BinRegistry Registry) Setup()
    {
        var fs = new MockFileSystem();
        return (fs, new BinRegistry(fs, @"C:\data\bin-roots.json"));
    }

    // Puts one item in the trash the way DeleteExecutor does, and writes its manifest line.
    private static void Trash(MockFileSystem fs, string name, string content, DateTimeOffset at, string batch = "20261101-100000-aaaaaa", string root = Root)
    {
        var trashed = $@"{root}\{batch}\{name}";
        fs.AddFile(trashed, new MockFileData(content));
        fs.File.AppendAllText($@"{root}\manifest.jsonl", Line($@"\\nas\media\show\{name}", trashed, at));
    }

    [Fact]
    public void Registry_RemembersRoots_AcrossInstances_AndNeverTwice()
    {
        var (fs, registry) = Setup();

        registry.Add(P(Root));
        registry.Add(P(Root.ToUpperInvariant()));

        Assert.Equal([Root], new BinRegistry(fs, @"C:\data\bin-roots.json").Roots.Select(r => r.Value));
    }

    [Fact]
    public void Registry_ADamagedFile_JustForgetsTheRoots()
    {
        var (fs, registry) = Setup();
        fs.AddFile(@"C:\data\bin-roots.json", new MockFileData("{ nope"));

        Assert.Empty(registry.Roots);
        registry.Add(P(Root));
        Assert.Single(registry.Roots);
    }

    [Fact]
    public void Manifest_SkipsBadLines_AndReadsTheRest()
    {
        var (fs, _) = Setup();
        Trash(fs, "a.mkv", "a", Now);
        fs.File.AppendAllText($@"{Root}\manifest.jsonl", "this is not json\n{\"original\":1}\n\n");
        Trash(fs, "b.mkv", "b", Now);

        var entries = BinManifest.Read(fs, P(Root));

        Assert.Equal(["a.mkv", "b.mkv"], entries.Select(e => P(e.Original).Name));
    }

    [Fact]
    public void Manifest_RemovingTheLastLine_RemovesTheFile()
    {
        var (fs, _) = Setup();
        Trash(fs, "a.mkv", "a", Now);
        var trashed = $@"{Root}\20261101-100000-aaaaaa\a.mkv";

        BinManifest.Remove(fs, P(Root), [trashed]);

        Assert.False(fs.File.Exists($@"{Root}\manifest.jsonl"));
    }

    [Theory]
    [InlineData(@"\\nas\media\.leviathan-trash\20261101-1\a.mkv", @"\\nas\media\.leviathan-trash")]
    [InlineData(@"\\nas\media\show\.Leviathan-Trash\x\a.mkv", @"\\nas\media\show\.Leviathan-Trash")]
    public void RootOf_FindsTheTrashFolderAbovethItem(string item, string expected) =>
        Assert.Equal(expected, BinManifest.RootOf(P(item))!.Value);

    [Fact]
    public void RootOf_AnItemInANasBin_HasNone() =>
        Assert.Null(BinManifest.RootOf(P(@"\\nas\media\#recycle\show\a.mkv")));

    [Fact]
    public void Scan_ListsFilesAndFolders_NewestFirst_AndLeavesOutWhatIsGone()
    {
        var (fs, registry) = Setup();
        registry.Add(P(Root));
        Trash(fs, "old.mkv", "12345", Now.AddDays(-9));
        Trash(fs, "new.mkv", "1234567890", Now.AddDays(-1));
        fs.AddFile($@"{Root}\20261102-1\season\ep1.mkv", new MockFileData("abc"));
        fs.File.AppendAllText($@"{Root}\manifest.jsonl", Line(@"\\nas\media\season", $@"{Root}\20261102-1\season", Now.AddDays(-3)));
        fs.File.AppendAllText($@"{Root}\manifest.jsonl", Line(@"\\nas\media\gone.mkv", $@"{Root}\20261101-9\gone.mkv", Now));

        var scan = new BinScanner(registry).Scan(measureFolders: true, CancellationToken.None);

        Assert.Equal(["new.mkv", "season", "old.mkv"], scan.Items.Select(i => i.Name));
        Assert.Equal([10L, 3L, 5L], scan.Items.Select(i => i.SizeBytes));
        Assert.True(scan.Items.Single(i => i.Name == "season").IsFolder);
        Assert.Empty(scan.Unreachable);
    }

    [Fact]
    public void Scan_WithoutMeasuring_LeavesAFoldersSizeUnknown()
    {
        var (fs, registry) = Setup();
        registry.Add(P(Root));
        fs.AddFile($@"{Root}\b\season\ep1.mkv", new MockFileData("abc"));
        fs.File.AppendAllText($@"{Root}\manifest.jsonl", Line(@"\\nas\media\season", $@"{Root}\b\season", Now));

        var scan = new BinScanner(registry).Scan(measureFolders: false, CancellationToken.None);

        Assert.Equal(-1, Assert.Single(scan.Items).SizeBytes);
    }

    [Fact]
    public void Scan_ARootThatCannotBeReached_IsReported_AndDoesNotHideTheOthers()
    {
        var (fs, registry) = Setup();
        registry.Add(P(Root));
        registry.Add(P(@"\\gone\share\.leviathan-trash"));
        Trash(fs, "a.mkv", "a", Now);

        var scan = new BinScanner(registry).Scan(false, CancellationToken.None);

        Assert.Single(scan.Items);
        Assert.Equal([@"\\gone\share\.leviathan-trash"], scan.Unreachable.Select(r => r.Value));
    }

    private static BinItem Item(string name, int daysAgo, long size) =>
        new(@"\\nas\media\" + name, P($@"{Root}\b\{name}"), Now.AddDays(-daysAgo), size, false, P(Root));

    [Fact]
    public void Retention_PurgesWhatIsOlderThanTheLimit()
    {
        var items = new[] { Item("a", 40, 1), Item("b", 29, 1), Item("c", 30, 1) };

        var purge = BinRetention.Expired(items, Now, maxAgeDays: 30, maxBytes: 0);

        Assert.Equal(["a", "c"], purge.Select(i => i.Name).Order());
    }

    [Fact]
    public void Retention_ThenTrimsTheOldestUntilUnderTheSizeLimit()
    {
        var items = new[] { Item("a", 5, 400), Item("b", 3, 400), Item("c", 1, 400) };

        var purge = BinRetention.Expired(items, Now, maxAgeDays: 30, maxBytes: 500);

        Assert.Equal(["a", "b"], purge.Select(i => i.Name));
    }

    [Fact]
    public void Retention_ZeroLimits_AreOff_AndAnUnmeasuredFolderIsNeverPurgedForSizeAlone()
    {
        var items = new[] { Item("a", 400, 100), Item("folder", 2, -1) };

        Assert.Empty(BinRetention.Expired(items, Now, 0, 0));
        Assert.Empty(BinRetention.Expired([items[1]], Now, 30, 1));
    }

    private static async Task<(JobSnapshot Job, IReadOnlyList<ItemOutcome> Items)> RunAsync(IJobExecutor executor, JobRequest request)
    {
        await using var host = new JobHost(new JobScheduler(), [executor], new InMemoryJobJournal());
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

    [Fact]
    public async Task Purge_DeletesThePickedItems_DropsTheirManifestLines_AndTidiesTheEmptyBatchFolder()
    {
        var (fs, registry) = Setup();
        registry.Add(P(Root));
        Trash(fs, "a.mkv", "a", Now, batch: "b1");
        Trash(fs, "b.mkv", "b", Now, batch: "b2");

        var (job, items) = await RunAsync(new PurgeExecutor(fs, registry), new JobRequest(JobKind.Purge, [$@"{Root}\b1\a.mkv"], null));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(ItemStatus.Done, items[0].Status);
        Assert.False(fs.Directory.Exists($@"{Root}\b1"));
        Assert.True(fs.File.Exists($@"{Root}\b2\b.mkv"));
        Assert.Equal(["b.mkv"], BinManifest.Read(fs, P(Root)).Select(e => P(e.Original).Name));
    }

    [Theory]
    [InlineData(@"\\nas\media\show\ep1.mkv")]
    [InlineData(@"\\nas\media\.leviathan-trash")]
    [InlineData(@"\\nas\media\#recycle")]
    public async Task Purge_NeverTouchesALiveFile_OrABinFolderItself(string path)
    {
        var (fs, registry) = Setup();
        fs.AddFile(@"\\nas\media\show\ep1.mkv", new MockFileData("v"));
        fs.AddDirectory(@"\\nas\media\.leviathan-trash");
        fs.AddDirectory(@"\\nas\media\#recycle");

        var (job, items) = await RunAsync(new PurgeExecutor(fs, registry), new JobRequest(JobKind.Purge, [path], null));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(ItemStatus.Failed, items[0].Status);
        Assert.True(fs.File.Exists(@"\\nas\media\show\ep1.mkv"));
        Assert.True(fs.Directory.Exists(@"\\nas\media\.leviathan-trash"));
        Assert.True(fs.Directory.Exists(@"\\nas\media\#recycle"));
    }

    [Fact]
    public async Task Purge_ByPolicy_RemovesOldAndOversizedItems_AndSaysWhenThereIsNothingToDo()
    {
        var (fs, registry) = Setup();
        registry.Add(P(Root));
        Trash(fs, "old.mkv", "xx", Now.AddDays(-40), batch: "b1");
        Trash(fs, "fresh.mkv", "yy", Now.AddDays(-1), batch: "b2");
        var time = new FixedNow();
        var policy = new JobRequest(JobKind.Purge, [], null) { PurgePolicy = new PurgePolicy(30, 0) };

        var (job, items) = await RunAsync(new PurgeExecutor(fs, registry, time), policy);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([$@"{Root}\b1\old.mkv"], items.Select(i => i.Source));
        Assert.True(fs.File.Exists($@"{Root}\b2\fresh.mkv"));

        var (_, again) = await RunAsync(new PurgeExecutor(fs, registry, time), policy);
        Assert.Empty(again);
    }

    private sealed class FixedNow : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Restore_ThenTakesTheItemOutOfTheManifest_SoTheBinDoesNotListItAgain()
    {
        var (fs, registry) = Setup();
        registry.Add(P(Root));
        Trash(fs, "a.mkv", "a", Now, batch: "b1");
        fs.AddDirectory(@"\\nas\media\show");

        var request = new JobRequest(JobKind.Restore, [$@"{Root}\b1\a.mkv"], null) { Targets = [@"\\nas\media\show\a.mkv"] };
        var (job, _) = await RunAsync(new RestoreExecutor(fs, registry), request);

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(fs.File.Exists(@"\\nas\media\show\a.mkv"));
        Assert.Empty(new BinScanner(registry).Scan(false, CancellationToken.None).Items);
        Assert.False(fs.File.Exists($@"{Root}\manifest.jsonl"));
    }

    [Fact]
    public async Task Delete_RemembersItsTrashFolder_SoTheBinCanFindTheItem()
    {
        var (fs, registry) = Setup();
        fs.AddFile(@"\\nas\media\show\ep1.mkv", new MockFileData("v"));
        var executor = new DeleteExecutor(fs, new NoBin(), bin: registry);

        var (job, items) = await RunAsync(executor, new JobRequest(JobKind.Delete, [@"\\nas\media\show\ep1.mkv"], null));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([Root], registry.Roots.Select(r => r.Value));
        var found = Assert.Single(new BinScanner(registry).Scan(false, CancellationToken.None).Items);
        Assert.Equal(items[0].Result, found.Trashed.Value);
        Assert.Equal(@"\\nas\media\show\ep1.mkv", found.Original);
    }

    private sealed class NoBin : IRecycleBin
    {
        public Task RecycleAsync(LocationPath path, CancellationToken cancellationToken) => throw new IOException("no bin");
    }
}
