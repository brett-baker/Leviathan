using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.SpaceMap;

namespace Leviathan.ViewModels.Tests;

/// <summary>The compact map of the status hub's Space tab (spec §14, §16.1).</summary>
public sealed class SpaceTabModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static FileEntry File(string name, long size) =>
        new(name, LocationPath.Parse(@"C:\here\" + name), FileEntryKind.File, size, Now, Now, FileAttributes.Normal);

    private static FileEntry Folder(string name) =>
        new(name, LocationPath.Parse(@"C:\here\" + name), FileEntryKind.Folder, 0, Now, Now, FileAttributes.Directory);

    [Fact]
    public void Build_MakesATilePerItem_WithFolderSizesFromTheStore()
    {
        var movies = Folder("movies");
        var docs = Folder("docs");
        var entries = new List<FileEntry> { File("big.zip", 400), movies, docs, File("a.txt", 100) };
        long? Bytes(FileEntry e) => e.Name == "movies" ? 900 : e.Name == "docs" ? 600 : null;

        var data = SpaceTabModel.Build(entries, Bytes, 400, 300, Now);

        Assert.Equal(4, data.Tiles.Count);
        Assert.Equal(["movies", "docs", "big.zip", "a.txt"], data.Items.Select(i => i.Entry.Name));
        Assert.Equal(0.45, data.Items[0].Fraction, 3);
        Assert.StartsWith(Leviathan.ViewModels.Folders.DisplayFormat.Size(2000), data.Summary, StringComparison.Ordinal);
        Assert.Contains("in 4 items", data.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FoldersNotCountedYet_AreListedAtTheEnd_AndSaidSo()
    {
        var entries = new List<FileEntry> { File("a.bin", 500), Folder("unknown") };

        var data = SpaceTabModel.Build(entries, _ => null, 400, 300, Now);

        Assert.Single(data.Tiles);
        Assert.Equal(["a.bin", "unknown"], data.Items.Select(i => i.Entry.Name));
        Assert.Equal("not counted yet", data.Items[1].SizeText);
        Assert.Contains("1 folders not counted yet", data.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AnEmptyFolder_SaysSo()
    {
        var data = SpaceTabModel.Build([], _ => null, 400, 300, Now);

        Assert.Empty(data.Tiles);
        Assert.Contains("Nothing here", data.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ListsNoMoreThanTheLimit_AndSkipsEmptyFilesAndLinks()
    {
        var entries = Enumerable.Range(0, 40).Select(i => File("f" + i, 100 + i)).ToList();
        entries.Add(File("empty", 0));
        entries.Add(new FileEntry("link", LocationPath.Parse(@"C:\here\link"), FileEntryKind.Link, 999, Now, Now, FileAttributes.ReparsePoint));

        var data = SpaceTabModel.Build(entries, _ => null, 800, 600, Now);

        Assert.Equal(SpaceTabModel.ItemsListed, data.Items.Count);
        Assert.Equal("f39", data.Items[0].Entry.Name);
        Assert.DoesNotContain(data.Tiles, t => t.Tile.Node.Name is "empty" or "link");
    }

    [Fact]
    public void EntryAt_FindsTheItemUnderAClick_ButNotForSmallItemsDrawnTogether()
    {
        var big = File("big.bin", 1_000_000);
        var entries = new List<FileEntry> { big };
        for (var i = 0; i < 50; i++)
        {
            entries.Add(File("tiny" + i, 1));
        }

        var data = SpaceTabModel.Build(entries, _ => null, 200, 100, Now);
        var bigTile = data.Tiles.Single(t => !t.Tile.IsRemainder && t.Tile.Node.Name == "big.bin").Tile;

        Assert.Equal(big, SpaceTabModel.EntryAt(data, bigTile.Bounds.X + 1, bigTile.Bounds.Y + 1));
        var rest = data.Tiles.Single(t => t.Tile.IsRemainder).Tile;
        Assert.Null(SpaceTabModel.EntryAt(data, rest.Bounds.X + 0.2, rest.Bounds.Y + 0.2));
        Assert.Null(SpaceTabModel.EntryAt(data, -1, -1));
    }
}
