using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

public sealed class RenameRulesTests
{
    private static FileEntry Entry(string name, FileEntryKind kind) =>
        new(name, LocationPath.Parse(@"C:\x").Combine(name), kind, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Theory]
    [InlineData("movie.mkv", 5)]
    [InlineData("archive.tar.gz", 11)]
    [InlineData(".gitignore", 10)]
    [InlineData("README", 6)]
    public void StemLength_File_ExcludesTheLastExtension(string name, int expected) =>
        Assert.Equal(expected, RenameRules.StemLength(Entry(name, FileEntryKind.File)));

    [Fact]
    public void StemLength_Folder_IsTheWholeName() =>
        Assert.Equal(9, RenameRules.StemLength(Entry("v1.2 beta", FileEntryKind.Folder)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    public void Validate_BadNames_SayWhy(string name) => Assert.NotNull(RenameRules.Validate(name));

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData(".gitignore")]
    [InlineData("a b (2).txt")]
    [InlineData("console.txt")]
    public void Validate_GoodNames_PassWithNoMessage(string name) => Assert.Null(RenameRules.Validate(name));

    [Fact]
    public void Validate_TooLong_IsRejected() => Assert.NotNull(RenameRules.Validate(new string('a', 256)));
}

public sealed class RenameServiceTests
{
    private static MockFileSystem Disk()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\a\one.txt", new MockFileData("1"));
        disk.AddFile(@"C:\a\two.txt", new MockFileData("2"));
        disk.AddDirectory(@"C:\a\dir");
        return disk;
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public async Task RenameAsync_File_MovesItAndReturnsTheNewLocation()
    {
        var disk = Disk();

        var result = await new RenameService(disk).RenameAsync(P(@"C:\a\one.txt"), "three.txt", CancellationToken.None);

        Assert.Equal(P(@"C:\a\three.txt"), result);
        Assert.False(disk.File.Exists(@"C:\a\one.txt"));
        Assert.Equal("1", disk.File.ReadAllText(@"C:\a\three.txt"));
    }

    [Fact]
    public async Task RenameAsync_Folder_Works()
    {
        var disk = Disk();

        await new RenameService(disk).RenameAsync(P(@"C:\a\dir"), "renamed", CancellationToken.None);

        Assert.True(disk.Directory.Exists(@"C:\a\renamed"));
        Assert.False(disk.Directory.Exists(@"C:\a\dir"));
    }

    [Fact]
    public async Task RenameAsync_OnlyTheCaseChanges_IsAllowed()
    {
        var disk = Disk();

        var result = await new RenameService(disk).RenameAsync(P(@"C:\a\one.txt"), "ONE.txt", CancellationToken.None);

        Assert.Equal("ONE.txt", result.Name);
    }

    [Fact]
    public async Task RenameAsync_NameTaken_FailsAndLeavesBothAlone()
    {
        var disk = Disk();

        var ex = await Assert.ThrowsAsync<IOException>(() => new RenameService(disk).RenameAsync(P(@"C:\a\one.txt"), "two.txt", CancellationToken.None));

        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.Equal("1", disk.File.ReadAllText(@"C:\a\one.txt"));
        Assert.Equal("2", disk.File.ReadAllText(@"C:\a\two.txt"));
    }

    [Fact]
    public async Task RenameAsync_SourceGone_Fails() =>
        await Assert.ThrowsAsync<FileNotFoundException>(() => new RenameService(Disk()).RenameAsync(P(@"C:\a\missing.txt"), "x.txt", CancellationToken.None));

    [Fact]
    public async Task RenameAsync_InvalidName_Fails() =>
        await Assert.ThrowsAsync<ArgumentException>(() => new RenameService(Disk()).RenameAsync(P(@"C:\a\one.txt"), "a:b", CancellationToken.None));
}

public sealed class RenameCommandTests
{
    private static (WindowCommandsTests.Harness Harness, MockFileSystem Disk) Make()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\a\1.txt", new MockFileData("x"));
        disk.AddDirectory(@"C:\a\b");
        return (new WindowCommandsTests.Harness(renamer: new RenameService(disk)), disk);
    }

    private static async Task<FileEntry> SelectAsync(WindowCommandsTests.Harness h, string name)
    {
        await h.Ready;
        var entry = h.Tab.Items.Rows.First(r => r.Name == name);
        h.Tab.Selection.Click(entry);
        return entry;
    }

    [Fact]
    public async Task EditRename_ShowsTheBoxOnTheOnlySelectedItem_AndF2IsBound()
    {
        var (h, _) = Make();
        var entry = await SelectAsync(h, "1.txt");

        Assert.Equal(CommandResult.Executed, await h.Run(CommandIds.EditRename));

        Assert.Equal(entry.Location, h.Tab.Renaming);
        Assert.Equal(CommandIds.EditRename, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse("F2"), isSplit: false));
    }

    [Fact]
    public async Task EditRename_NeedsExactlyOneItem_AndARenamer()
    {
        var (h, _) = Make();
        await h.Ready;
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditRename));

        h.Tab.Selection.SelectAll();
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditRename));

        var without = new WindowCommandsTests.Harness();
        await without.Ready;
        without.Tab.Selection.Click(without.Tab.Items.Rows[0]);
        Assert.Equal(CommandResult.Disabled, await without.Run(CommandIds.EditRename));
    }

    [Fact]
    public async Task RenameCommit_RenamesOnDisk_ClosesTheBox_AndReports()
    {
        var (h, disk) = Make();
        await SelectAsync(h, "1.txt");
        await h.Run(CommandIds.EditRename);

        await h.Run(CommandIds.RenameCommit, "renamed.txt");

        Assert.True(disk.File.Exists(@"C:\a\renamed.txt"));
        Assert.Null(h.Tab.Renaming);
        Assert.Equal("Renamed to renamed.txt", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task RenameCommit_SameName_JustClosesTheBox()
    {
        var (h, disk) = Make();
        await SelectAsync(h, "1.txt");
        await h.Run(CommandIds.EditRename);

        await h.Run(CommandIds.RenameCommit, "1.txt");

        Assert.Null(h.Tab.Renaming);
        Assert.True(disk.File.Exists(@"C:\a\1.txt"));
    }

    [Fact]
    public async Task RenameCommit_InvalidName_KeepsTheBoxOpenAndSaysWhy()
    {
        var (h, disk) = Make();
        var entry = await SelectAsync(h, "1.txt");
        await h.Run(CommandIds.EditRename);

        await h.Run(CommandIds.RenameCommit, "bad:name");

        Assert.Equal(entry.Location, h.Tab.Renaming);
        Assert.Contains("cannot contain", h.Shell.Messages[^1], StringComparison.Ordinal);
        Assert.True(disk.File.Exists(@"C:\a\1.txt"));
    }

    [Fact]
    public async Task RenameCommit_NameTaken_ClosesTheBoxAndExplains()
    {
        var (h, disk) = Make();
        await SelectAsync(h, "1.txt");
        await h.Run(CommandIds.EditRename);

        await h.Run(CommandIds.RenameCommit, "b");

        Assert.Null(h.Tab.Renaming);
        Assert.Contains("already exists", h.Shell.Messages[^1], StringComparison.Ordinal);
        Assert.True(disk.File.Exists(@"C:\a\1.txt"));
    }

    [Fact]
    public async Task RenameCommit_WithoutABox_IsDisabled()
    {
        var (h, _) = Make();
        await h.Ready;

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.RenameCommit, "x.txt"));
    }

    [Fact]
    public async Task RenameCommit_Tab_MovesTheBoxToTheNextItem()
    {
        var (h, _) = Make();
        await SelectAsync(h, "b");
        await h.Run(CommandIds.EditRename);
        var next = h.Tab.Items.Rows[1];

        await h.Run(CommandIds.RenameCommit, "b", true);

        Assert.Equal(next.Location, h.Tab.Renaming);
        Assert.Equal([next.Location], h.Tab.Selection.SelectedEntries.Select(e => e.Location));
    }

    [Fact]
    public async Task ChangingTheLayout_ClosesTheBox()
    {
        var (h, _) = Make();
        await SelectAsync(h, "1.txt");
        await h.Run(CommandIds.EditRename);

        h.Tab.SetLayout(Views.ViewLayout.Thumbnails);

        Assert.Null(h.Tab.Renaming);
    }

    [Fact]
    public async Task Navigating_ClosesTheBox()
    {
        var (h, _) = Make();
        await SelectAsync(h, "1.txt");
        await h.Run(CommandIds.EditRename);

        await h.Tab.NavigateAsync(LocationPath.Parse(@"C:\v"), CancellationToken.None);

        Assert.Null(h.Tab.Renaming);
    }
}

public sealed class RubberBandAndKeyboardTests
{
    private static readonly LocationPath Root = LocationPath.Parse(@"C:\x");

    private static (SnapshotList<FileEntry> List, SelectionModel Model) Make(int count)
    {
        var list = new SnapshotList<FileEntry>();
        list.Replace([.. Enumerable.Range(0, count).Select(i => new FileEntry($"f{i}", Root.Combine($"f{i}"), FileEntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal))]);
        return (list, new SelectionModel(list));
    }

    [Fact]
    public void ApplyRubberBand_Indexes_SelectsThemAndKeepsTheEarlierSelectionWhenAsked()
    {
        var (list, model) = Make(10);
        model.Click(list[0]);
        var before = model.Snapshot();

        model.ApplyRubberBand([3, 4, 5], before);
        Assert.Equal(["f0", "f3", "f4", "f5"], model.SelectedEntries.Select(e => e.Name));

        model.ApplyRubberBand([6]);
        Assert.Equal(["f6"], model.SelectedEntries.Select(e => e.Name));
    }

    [Fact]
    public void ApplyRubberBand_Indexes_IgnoresOutOfRangeAndRaisesChangedOnlyOnChange()
    {
        var (_, model) = Make(3);
        var raised = 0;
        model.Changed += (_, _) => raised++;

        model.ApplyRubberBand([1, 99, -4]);
        model.ApplyRubberBand([1]);

        Assert.Equal(1, raised);
        Assert.Equal(1, model.Count);
    }

    [Fact]
    public void ToggleFocus_FlipsTheFocusedItem_AndNeedsAFocus()
    {
        var (list, model) = Make(3);
        Assert.False(model.ToggleFocus());

        model.Click(list[1]);
        Assert.True(model.ToggleFocus());
        Assert.Equal(0, model.Count);
        Assert.True(model.ToggleFocus());
        Assert.Equal(1, model.Count);
    }

    [Fact]
    public void IndexesIn_Thumbnails_ReturnsTheCellsUnderTheBand()
    {
        // 3 per line, cells 100x100 with 4 px gaps: cell (x, y) starts at 104 * x and 104 * y.
        var hit = ItemFlow.IndexesIn(90, 90, 210, 110, count: 20, itemWidth: 100, itemHeight: 100, perLine: 3, columnMajor: false);

        Assert.Equal([0, 1, 2, 3, 4, 5], hit);
    }

    [Fact]
    public void IndexesIn_Thumbnails_StopsAtTheLastItemAndTheLastColumn()
    {
        var hit = ItemFlow.IndexesIn(0, 0, 1000, 1000, count: 7, itemWidth: 100, itemHeight: 100, perLine: 3, columnMajor: false);

        Assert.Equal(Enumerable.Range(0, 7), hit);
    }

    [Fact]
    public void IndexesIn_List_RunsDownColumns()
    {
        // 4 per column: column 1 holds items 4..7.
        var hit = ItemFlow.IndexesIn(250, 0, 260, 60, count: 20, itemWidth: 240, itemHeight: 28, perLine: 4, columnMajor: true);

        Assert.Equal([4, 5], hit);
    }

    [Fact]
    public void IndexesIn_BandInTheGapsOrOutside_FindsNothing()
    {
        Assert.Empty(ItemFlow.IndexesIn(-50, -50, -10, -10, 5, 100, 100, 3, false));
        Assert.Empty(ItemFlow.IndexesIn(0, 500, 50, 600, 3, 100, 100, 3, false));
    }
}
