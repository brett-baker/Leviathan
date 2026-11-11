using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Engine.Duplicates;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Engine.Tests;

/// <summary>Finding files with identical contents (spec §15.2).</summary>
public sealed class DuplicateFinderTests
{
    private const int Mb = 1024 * 1024;

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static MockFileData Data(byte[] bytes) => new(bytes);

    private static Task<DuplicateResult> Find(MockFileSystem disk, DuplicateOptions? options = null, IProgress<DuplicateProgress>? progress = null, CancellationToken token = default, params string[] roots) =>
        new DuplicateFinder(disk).FindAsync(roots.Length == 0 ? [@"C:\data"] : roots, options ?? new DuplicateOptions(), progress, token);

    [Fact]
    public async Task SameContentsInDifferentFolders_AreOneGroup_AndDifferentContentsOfTheSameSizeAreNot()
    {
        var disk = new MockFileSystem();
        var a = Bytes(1000, 1);
        disk.AddFile(@"C:\data\one\a.bin", Data(a));
        disk.AddFile(@"C:\data\two\copy of a.bin", Data(a));
        disk.AddFile(@"C:\data\two\other.bin", Data(Bytes(1000, 2)));
        disk.AddFile(@"C:\data\two\unique.bin", Data(Bytes(999, 3)));

        var result = await Find(disk);

        var group = Assert.Single(result.Groups);
        Assert.Equal(1000, group.Size);
        Assert.Equal([@"C:\data\one\a.bin", @"C:\data\two\copy of a.bin"], group.Paths.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(4, result.FilesScanned);
        Assert.Equal(1000, group.Wasted);
    }

    [Fact]
    public async Task BigFilesThatDifferOnlyInTheMiddle_AreToldApartByTheFullHash()
    {
        var disk = new MockFileSystem();
        var first = Bytes(5 * Mb, 7);
        var second = (byte[])first.Clone();
        second[2 * Mb + 123]++; // Outside the first and last megabyte: the partial hash cannot see it.
        disk.AddFile(@"C:\data\a.bin", Data(first));
        disk.AddFile(@"C:\data\b.bin", Data(second));
        disk.AddFile(@"C:\data\c.bin", Data(first));

        var result = await Find(disk);

        var group = Assert.Single(result.Groups);
        Assert.Equal([@"C:\data\a.bin", @"C:\data\c.bin"], group.Paths.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task FilesThatDifferInTheirFirstBytes_AreNeverReadInFull()
    {
        var disk = new MockFileSystem();
        var first = Bytes(10 * Mb, 1);
        var second = Bytes(10 * Mb, 2);
        disk.AddFile(@"C:\data\a.bin", Data(first));
        disk.AddFile(@"C:\data\b.bin", Data(second));

        var result = await Find(disk);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task AFileNoBiggerThanBothEdges_IsComparedInOneRead()
    {
        var disk = new MockFileSystem();
        var content = Bytes(2 * Mb, 4);
        disk.AddFile(@"C:\data\a.bin", Data(content));
        disk.AddFile(@"C:\data\b.bin", Data(content));
        var reports = new List<DuplicateProgress>();

        var result = await Find(disk, progress: new Collect(reports));

        Assert.Single(result.Groups);
        Assert.DoesNotContain(reports, r => r.Stage == DuplicateStage.FullHashing && r.Current is not null);
        Assert.Equal(4L * Mb, reports[^1].BytesRead);
    }

    [Fact]
    public async Task EmptyFiles_AreNotDuplicates_AndTheMinimumSizeAndTypeFiltersApply()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\data\e1.txt", new MockFileData(string.Empty));
        disk.AddFile(@"C:\data\e2.txt", new MockFileData(string.Empty));
        disk.AddFile(@"C:\data\small1.txt", Data(Bytes(100, 1)));
        disk.AddFile(@"C:\data\small2.txt", Data(Bytes(100, 1)));
        disk.AddFile(@"C:\data\big1.mkv", Data(Bytes(5000, 2)));
        disk.AddFile(@"C:\data\big2.MKV", Data(Bytes(5000, 2)));
        disk.AddFile(@"C:\data\big1.txt", Data(Bytes(5000, 2)));
        disk.AddFile(@"C:\data\big2.txt", Data(Bytes(5000, 2)));

        // No filter: the two small files, and the four big ones (same bytes under two types) are two groups; the empty files are none.
        Assert.Equal(2, (await Find(disk)).Groups.Count);

        var bigOnly = (await Find(disk, new DuplicateOptions(MinimumBytes: 1000))).Groups.Single();
        Assert.Equal(4, bigOnly.Paths.Count);

        var mkv = (await Find(disk, new DuplicateOptions(MinimumBytes: 1000, Extensions: ["mkv"]))).Groups.Single();
        Assert.Equal(["big1.mkv", "big2.MKV"], mkv.Paths.Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray());

        var txt = (await Find(disk, new DuplicateOptions(MinimumBytes: 1000, Extensions: [".txt"]))).Groups.Single();
        Assert.Equal(2, txt.Paths.Count);
    }

    [Fact]
    public async Task HiddenFilesAndFolders_AreLeftOutUnlessAsked()
    {
        var disk = new MockFileSystem();
        var content = Bytes(500, 5);
        disk.AddFile(@"C:\data\a.bin", Data(content));
        disk.AddFile(@"C:\data\.hidden\b.bin", Data(content));
        disk.AddDirectory(@"C:\data\.hidden");
        disk.File.SetAttributes(@"C:\data\.hidden\b.bin", FileAttributes.Hidden);
        disk.DirectoryInfo.New(@"C:\data\.hidden").Attributes |= FileAttributes.Hidden;

        Assert.Empty((await Find(disk)).Groups);
        Assert.Single((await Find(disk, new DuplicateOptions(IncludeHidden: true))).Groups);
    }

    [Fact]
    public async Task OverlappingRoots_DoNotMakeAFileItsOwnDuplicate()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\data\sub\a.bin", Data(Bytes(500, 5)));

        var result = await Find(disk, roots: [@"C:\data", @"C:\data\sub", @"C:\data\sub\a.bin"]);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.FilesScanned);
    }

    [Fact]
    public async Task ThreeCopies_AreOneGroupOfThree_AndTheBiggestWasteComesFirst()
    {
        var disk = new MockFileSystem();
        var small = Bytes(100, 1);
        var large = Bytes(900, 2);
        foreach (var n in new[] { "1", "2" })
        {
            disk.AddFile($@"C:\data\small{n}.bin", Data(small));
            disk.AddFile($@"C:\data\large{n}.bin", Data(large));
        }

        disk.AddFile(@"C:\data\small3.bin", Data(small));

        var result = await Find(disk);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(900, result.Groups[0].Size);
        Assert.Equal(200, result.Groups[1].Wasted);
        Assert.Equal(3, result.Groups[1].Paths.Count);
    }

    [Fact]
    public async Task AFolderThatCannotBeRead_IsReported_AndTheRestIsStillSearched()
    {
        var disk = new MockFileSystem();
        var content = Bytes(500, 5);
        disk.AddFile(@"C:\data\a.bin", Data(content));
        disk.AddFile(@"C:\data\b.bin", Data(content));
        disk.AddFile(@"C:\data\locked\c.bin", new MockFileData(content) { AllowedFileShare = FileShare.None });

        var result = await Find(disk);

        Assert.Single(result.Groups);
        Assert.Equal(2, result.Groups[0].Paths.Count);
        Assert.Contains(result.Unreadable, u => u.Contains("c.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledSearch_Throws_NotAnEmptyAnswer()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\data\a.bin", Data(Bytes(500, 5)));
        disk.AddFile(@"C:\data\b.bin", Data(Bytes(500, 5)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Find(disk, token: cts.Token));
    }

    [Fact]
    public async Task TheJob_ReportsEveryDuplicateWithItsGroupKey_AndANote()
    {
        var disk = new MockFileSystem();
        var content = Bytes(500, 5);
        disk.AddFile(@"C:\data\a.bin", Data(content));
        disk.AddFile(@"C:\data\b.bin", Data(content));
        disk.AddFile(@"C:\data\c.bin", Data(Bytes(500, 6)));
        await using var host = new JobHost(new JobScheduler(), [new FindDuplicatesExecutor(disk)], new InMemoryJobJournal());
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.FindDuplicates, [@"C:\data"], null) { DuplicateMinimumBytes = 100 }, JobLane.Background, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "the job did not finish");
            await Task.Delay(5);
        }

        Assert.Equal(JobState.Completed, host.Snapshot(host.Scheduler.Find(job.Id)!).State);
        var items = host.GetResult(job.Id)!.Items;
        Assert.Equal([@"C:\data\a.bin", @"C:\data\b.bin"], items.Select(i => i.Source).Order(StringComparer.Ordinal).ToArray());
        Assert.Single(items.Select(i => i.Result).Distinct());
        Assert.StartsWith("500:", items[0].Result, StringComparison.Ordinal);
        Assert.All(items, i => Assert.Equal(ItemStatus.Done, i.Status));
    }

    private sealed class Collect(List<DuplicateProgress> into) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value) => into.Add(value);
    }
}
