using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Leviathan.Core.Compare;

namespace Leviathan.Core.Tests;

/// <summary>Comparing two folders (spec §15.5).</summary>
public sealed class FolderComparerTests
{
    private static readonly DateTime T = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MockFileData File(string text, DateTime? modified = null) => new(Encoding.ASCII.GetBytes(text)) { LastWriteTime = modified ?? T };

    private static async Task<CompareResult> CompareAsync(MockFileSystem disk, CompareOptions? options = null, IProgress<CompareProgress>? progress = null, CancellationToken token = default) =>
        await new FolderComparer(disk).CompareAsync(@"L:\", @"R:\", options ?? new CompareOptions(), progress, token);

    private static MockFileSystem Disk()
    {
        var disk = new MockFileSystem();
        disk.AddDirectory(@"L:\");
        disk.AddDirectory(@"R:\");
        return disk;
    }

    private static CompareStatus StatusOf(CompareResult result, string path) => result.Entries.Single(e => e.Path == path).Status;

    [Fact]
    public async Task EveryKindOfDifference_IsTold()
    {
        var disk = Disk();
        disk.AddFile(@"L:\same.txt", File("abc"));
        disk.AddFile(@"R:\same.txt", File("abc"));
        disk.AddFile(@"L:\only-left.txt", File("x"));
        disk.AddFile(@"R:\only-right.txt", File("y"));
        disk.AddFile(@"L:\newer-left.txt", File("new", T.AddHours(5)));
        disk.AddFile(@"R:\newer-left.txt", File("old"));
        disk.AddFile(@"L:\newer-right.txt", File("old"));
        disk.AddFile(@"R:\newer-right.txt", File("new", T.AddHours(5)));
        disk.AddFile(@"L:\clash.txt", File("short"));
        disk.AddFile(@"R:\clash.txt", File("much longer"));

        var result = await CompareAsync(disk);

        Assert.Equal(CompareStatus.Identical, StatusOf(result, "same.txt"));
        Assert.Equal(CompareStatus.LeftOnly, StatusOf(result, "only-left.txt"));
        Assert.Equal(CompareStatus.RightOnly, StatusOf(result, "only-right.txt"));
        Assert.Equal(CompareStatus.NewerLeft, StatusOf(result, "newer-left.txt"));
        Assert.Equal(CompareStatus.NewerRight, StatusOf(result, "newer-right.txt"));
        Assert.Equal(CompareStatus.Different, StatusOf(result, "clash.txt"));
    }

    [Fact]
    public async Task Subfolders_AreWalked_FoldersAreEntries_AndNamesIgnoreCase()
    {
        var disk = Disk();
        disk.AddFile(@"L:\Docs\Sub\a.txt", File("1"));
        disk.AddFile(@"R:\docs\sub\A.TXT", File("1"));
        disk.AddDirectory(@"L:\empty");

        var result = await CompareAsync(disk);

        Assert.Equal(["docs", @"docs\sub", @"docs\sub\a.txt", "empty"], result.Entries.Select(e => e.Path.ToLowerInvariant()));
        Assert.All(result.Entries.Where(e => e.Path.StartsWith("Docs", StringComparison.OrdinalIgnoreCase)), e => Assert.Equal(CompareStatus.Identical, e.Status));
        var empty = result.Entries.Single(e => e.Path == "empty");
        Assert.True(empty.IsDirectory);
        Assert.Equal(CompareStatus.LeftOnly, empty.Status);
    }

    [Fact]
    public async Task AFileOnOneSide_AndAFolderOnTheOther_IsATypeMismatch()
    {
        var disk = Disk();
        disk.AddFile(@"L:\thing", File("x"));
        disk.AddFile(@"R:\thing\inside.txt", File("y"));

        var result = await CompareAsync(disk);

        Assert.Equal(CompareStatus.TypeMismatch, StatusOf(result, "thing"));
        Assert.Equal(CompareStatus.RightOnly, StatusOf(result, @"thing\inside.txt"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task TimesWithinTheTolerance_AreTheSame(int seconds, bool same)
    {
        var disk = Disk();
        disk.AddFile(@"L:\a.txt", File("abc", T.AddSeconds(seconds)));
        disk.AddFile(@"R:\a.txt", File("abc"));

        var status = StatusOf(await CompareAsync(disk), "a.txt");

        Assert.Equal(same ? CompareStatus.Identical : CompareStatus.NewerLeft, status);
    }

    [Fact]
    public async Task ADaylightSavingHour_IsOnlyForgivenWhenAsked()
    {
        var disk = Disk();
        disk.AddFile(@"L:\a.txt", File("abc", T.AddHours(1)));
        disk.AddFile(@"R:\a.txt", File("abc"));

        Assert.Equal(CompareStatus.NewerLeft, StatusOf(await CompareAsync(disk), "a.txt"));
        Assert.Equal(CompareStatus.Identical, StatusOf(await CompareAsync(disk, new CompareOptions { IgnoreDaylightSaving = true }), "a.txt"));
    }

    [Fact]
    public async Task ByChecksum_ATouchedFile_IsStillIdentical_AndAChangedOneIsNot()
    {
        var disk = Disk();
        disk.AddFile(@"L:\touched.txt", File("same bytes", T.AddDays(3)));
        disk.AddFile(@"R:\touched.txt", File("same bytes"));
        disk.AddFile(@"L:\edited.txt", File("aaaa", T.AddDays(1)));
        disk.AddFile(@"R:\edited.txt", File("bbbb"));
        disk.AddFile(@"L:\mystery.txt", File("aaaa"));
        disk.AddFile(@"R:\mystery.txt", File("bbbb"));

        var result = await CompareAsync(disk, new CompareOptions { By = CompareBy.Checksum });

        Assert.Equal(CompareStatus.Identical, StatusOf(result, "touched.txt"));
        Assert.Equal(CompareStatus.NewerLeft, StatusOf(result, "edited.txt"));
        Assert.Equal(CompareStatus.Different, StatusOf(result, "mystery.txt"));
        Assert.NotNull(result.Entries.Single(e => e.Path == "edited.txt").LeftChecksum);
        Assert.NotEqual(result.Entries.Single(e => e.Path == "edited.txt").LeftChecksum, result.Entries.Single(e => e.Path == "edited.txt").RightChecksum);
    }

    [Fact]
    public async Task ByChecksum_FilesOfDifferentSize_AreNotRead()
    {
        var disk = Disk();
        disk.AddFile(@"L:\a.txt", File("short", T.AddDays(1)));
        disk.AddFile(@"R:\a.txt", File("much longer"));

        var result = await CompareAsync(disk, new CompareOptions { By = CompareBy.Checksum });

        var entry = Assert.Single(result.Entries);
        Assert.Equal(CompareStatus.NewerLeft, entry.Status);
        Assert.Null(entry.LeftChecksum);
    }

    [Fact]
    public async Task ByNameSizeDate_ReadsNoFile()
    {
        var disk = Disk();
        disk.AddFile(@"L:\a.txt", File("abc"));
        disk.AddFile(@"R:\a.txt", File("abc"));

        var result = await CompareAsync(disk);

        Assert.Null(Assert.Single(result.Entries).LeftChecksum);
    }

    [Fact]
    public async Task TheDefaultExcludes_LeaveOutBinsAndWorkFiles_AndTheListCanBeReplaced()
    {
        var disk = Disk();
        disk.AddFile(@"L:\keep.txt", File("1"));
        disk.AddFile(@"L:\#recycle\gone.txt", File("1"));
        disk.AddFile(@"L:\.leviathan-trash\x.txt", File("1"));
        disk.AddFile(@"L:\big.iso.leviathan-part", File("1"));
        disk.AddFile(@"L:\Thumbs.db", File("1"));

        var normal = await CompareAsync(disk);
        var custom = await CompareAsync(disk, new CompareOptions { Exclude = ["*.txt"] });

        Assert.Equal(["keep.txt"], normal.Entries.Select(e => e.Path));
        Assert.DoesNotContain(custom.Entries, e => e.Path.EndsWith(".txt", StringComparison.Ordinal));
        Assert.Contains(custom.Entries, e => e.Path == "Thumbs.db");
    }

    [Fact]
    public async Task HiddenFiles_CanBeLeftOut()
    {
        var disk = Disk();
        disk.AddFile(@"L:\visible.txt", File("1"));
        disk.AddFile(@"L:\hidden.txt", new MockFileData("1") { Attributes = FileAttributes.Hidden });

        var all = await CompareAsync(disk);
        var visible = await CompareAsync(disk, new CompareOptions { IncludeHidden = false });

        Assert.Equal(2, all.Entries.Count);
        Assert.Equal(["visible.txt"], visible.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Links_AreNotFollowed()
    {
        var disk = Disk();
        disk.AddFile(@"L:\real.txt", File("1"));
        disk.AddFile(@"L:\link.txt", new MockFileData("1") { Attributes = FileAttributes.ReparsePoint });

        var result = await CompareAsync(disk);

        Assert.Equal(["real.txt"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Counts_AndOrder_AreForTheWholeResult()
    {
        var disk = Disk();
        disk.AddFile(@"L:\b.txt", File("1"));
        disk.AddFile(@"L:\a.txt", File("1"));
        disk.AddFile(@"R:\a.txt", File("1"));
        disk.AddFile(@"R:\c.txt", File("1"));

        var result = await CompareAsync(disk);

        Assert.Equal(["a.txt", "b.txt", "c.txt"], result.Entries.Select(e => e.Path));
        Assert.Equal(1, result.Count(CompareStatus.Identical));
        Assert.Equal(1, result.Count(CompareStatus.LeftOnly));
        Assert.Equal(1, result.Count(CompareStatus.RightOnly));
    }

    [Fact]
    public async Task Progress_ReportsTheListingAndTheReading()
    {
        var disk = Disk();
        disk.AddFile(@"L:\a.txt", File("abc", T.AddDays(1)));
        disk.AddFile(@"R:\a.txt", File("abc"));
        var seen = new List<CompareProgress>();

        await CompareAsync(disk, new CompareOptions { By = CompareBy.Checksum }, new Inline(seen.Add));

        Assert.Contains(seen, p => p.ToHash == 1 && p.Current == "a.txt");
        var last = seen[^1];
        Assert.Equal((2, 1, 1), (last.Scanned, last.Hashed, last.ToHash));
    }

    [Fact]
    public async Task Cancelling_StopsTheComparison()
    {
        var disk = Disk();
        disk.AddFile(@"L:\a.txt", File("1"));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompareAsync(disk, token: cancel.Token));
    }

    [Fact]
    public async Task TwoEmptyFolders_HaveNothingToCompare()
    {
        var result = await CompareAsync(Disk());

        Assert.Empty(result.Entries);
        Assert.Empty(result.Problems);
    }

    private sealed class Inline(Action<CompareProgress> report) : IProgress<CompareProgress>
    {
        public void Report(CompareProgress value) => report(value);
    }
}
