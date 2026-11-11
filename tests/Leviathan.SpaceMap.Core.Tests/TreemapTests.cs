namespace Leviathan.SpaceMap.Core.Tests;

/// <summary>The squarified layout and the colouring of the space map (spec §14).</summary>
public sealed class TreemapTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static SpaceNode Folder(SpaceNode? parent, string name) => new(name, true, 0, DateTimeOffset.UnixEpoch, parent);

    private static SpaceNode File(SpaceNode parent, string name, long size, DateTimeOffset? modified = null)
    {
        var file = new SpaceNode(name, false, size, modified ?? Now, parent);
        parent.Add(file);
        return file;
    }

    private static SpaceNode Sub(SpaceNode parent, string name)
    {
        var sub = Folder(parent, name);
        parent.Add(sub);
        return sub;
    }

    private static SpaceNode RandomTree(int seed, int files)
    {
        var random = new Random(seed);
        var root = Folder(null, @"D:\");
        var folders = new List<SpaceNode> { root };
        for (var i = 0; i < files; i++)
        {
            var parent = folders[random.Next(folders.Count)];
            if (random.Next(8) == 0)
            {
                folders.Add(Sub(parent, "d" + i));
            }

            File(parent, $"f{i}.{(random.Next(3) == 0 ? "mkv" : "txt")}", 1 + (long)Math.Pow(random.NextDouble(), 4) * 10_000_000);
        }

        return root;
    }

    [Fact]
    public void Add_KeepsTheTotalsOfEveryFolderAbove()
    {
        var root = Folder(null, @"C:\");
        var sub = Sub(root, "sub");
        File(sub, "a.bin", 100);
        File(root, "b.bin", 50);

        Assert.Equal(150, root.Size);
        Assert.Equal(100, sub.Size);
        Assert.Equal(2, root.Files);
        Assert.Equal(@"C:\sub\a.bin", sub.Children[0].FullPath);
    }

    [Fact]
    public void Remove_TakesTheSizeOffEveryFolderAbove()
    {
        var root = Folder(null, @"C:\");
        var sub = Sub(root, "sub");
        var file = File(sub, "a.bin", 100);
        File(root, "b.bin", 50);

        Assert.True(sub.Remove(file));

        Assert.Equal(50, root.Size);
        Assert.Equal(0, sub.Size);
        Assert.False(sub.Remove(file));
    }

    [Fact]
    public void Modified_OfAFolder_IsTheNewestFileBelowIt()
    {
        var root = Folder(null, @"C:\");
        var sub = Sub(root, "sub");
        File(sub, "old", 1, Now.AddYears(-3));
        File(sub, "new", 1, Now.AddDays(-2));

        Assert.Equal(Now.AddDays(-2), root.Modified);
        Assert.Equal(Now.AddDays(-2), sub.Modified);
    }

    [Fact]
    public void Layout_GivesEachFileAnAreaInProportionToItsBytes()
    {
        var root = Folder(null, @"C:\");
        File(root, "big", 600);
        File(root, "mid", 300);
        File(root, "small", 100);
        var bounds = new TileRect(0, 0, 1000, 500);

        var tiles = Treemap.Layout(root, bounds);

        Assert.Equal(3, tiles.Count);
        foreach (var tile in tiles)
        {
            Assert.Equal(bounds.Area * tile.Node.Size / 1000, tile.Bounds.Area, 3);
        }
    }

    [Fact]
    public void Layout_FillsTheSpace_WithNoOverlapAndNoGap()
    {
        var root = RandomTree(3, 400);
        var bounds = new TileRect(10, 20, 800, 600);

        var tiles = Treemap.Layout(root, bounds, new TreemapOptions { FolderPadding = 0, MinTileArea = 0 });

        Assert.Equal(bounds.Area, tiles.Sum(t => t.Bounds.Area), 3);
        foreach (var tile in tiles)
        {
            Assert.True(tile.Bounds.X >= bounds.X - 1e-6 && tile.Bounds.Right <= bounds.Right + 1e-6);
            Assert.True(tile.Bounds.Y >= bounds.Y - 1e-6 && tile.Bounds.Bottom <= bounds.Bottom + 1e-6);
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                Assert.False(Overlap(tiles[i].Bounds, tiles[j].Bounds), $"{tiles[i].Node.Name} overlaps {tiles[j].Node.Name}");
            }
        }
    }

    private static bool Overlap(TileRect a, TileRect b) =>
        Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 1e-6 && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y) > 1e-6;

    [Fact]
    public void Layout_KeepsTilesNearlySquare()
    {
        var root = Folder(null, @"C:\");
        var random = new Random(5);
        for (var i = 0; i < 200; i++)
        {
            File(root, "f" + i, 1 + random.Next(1, 1000));
        }

        var tiles = Treemap.Layout(root, new TileRect(0, 0, 800, 600));
        var ratios = tiles.Select(t => Math.Max(t.Bounds.Width / t.Bounds.Height, t.Bounds.Height / t.Bounds.Width)).Order().ToList();

        Assert.True(ratios[ratios.Count / 2] < 2.5, "the median tile should be close to square");
    }

    [Fact]
    public void Layout_PutsAFoldersFilesInsideTheFoldersRectangle()
    {
        var root = Folder(null, @"C:\");
        var sub = Sub(root, "sub");
        File(sub, "a", 400);
        File(sub, "b", 100);
        File(root, "c", 500);

        var tiles = Treemap.Layout(root, new TileRect(0, 0, 100, 100), new TreemapOptions { FolderPadding = 0 });
        var a = tiles.Single(t => t.Node.Name == "a").Bounds;
        var b = tiles.Single(t => t.Node.Name == "b").Bounds;
        var c = tiles.Single(t => t.Node.Name == "c").Bounds;

        Assert.Equal(5000, a.Area + b.Area, 3);
        Assert.Equal(5000, c.Area, 3);
        Assert.Equal(2, tiles.Single(t => t.Node.Name == "a").Depth);
        Assert.Equal(1, tiles.Single(t => t.Node.Name == "c").Depth);
        Assert.False(Overlap(a, c) || Overlap(b, c));
    }

    [Fact]
    public void Layout_DrawsItemsTooSmallToSeeTogether_AsOneRemainderTileOfTheirFolder()
    {
        var root = Folder(null, @"C:\");
        var huge = File(root, "huge", 1_000_000);
        var tiny = Sub(root, "tiny");
        File(tiny, "x", 1);
        File(tiny, "y", 1);
        File(root, "z", 5);

        var tiles = Treemap.Layout(root, new TileRect(0, 0, 100, 100), new TreemapOptions { MinTileArea = 6, FolderPadding = 0 });

        Assert.Equal(2, tiles.Count);
        Assert.Equal(huge, tiles.Single(t => !t.IsRemainder).Node);
        var rest = tiles.Single(t => t.IsRemainder);
        Assert.Equal(root, rest.Node);
        Assert.Equal(10_000.0 * 7 / 1_000_007, rest.Bounds.Area, 3); // tiny (2) + z (5), by the bytes they own
        Assert.Equal(rest, Treemap.TileOf(tiles, tiny));
    }

    [Fact]
    public void Layout_OfAMillionFiles_StaysAsSmallAsTheScreenAllows()
    {
        var root = Folder(null, @"D:\");
        var random = new Random(1);
        for (var f = 0; f < 2000; f++)
        {
            var folder = Sub(root, "f" + f);
            for (var i = 0; i < 500; i++)
            {
                File(folder, "x" + i, 1 + random.Next(1, 100_000));
            }
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var tiles = Treemap.Layout(root, new TileRect(0, 0, 1600, 900));

        Assert.Equal(1_000_000, root.Files);
        Assert.True(tiles.Count < 400_000, $"{tiles.Count} tiles");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"layout took {started.Elapsed}");
    }

    [Fact]
    public void Layout_HidesFilesBelowTheSizeFilter()
    {
        var root = Folder(null, @"C:\");
        File(root, "big", 1000);
        File(root, "small", 10);

        var tiles = Treemap.Layout(root, new TileRect(0, 0, 100, 100), new TreemapOptions { MinBytes = 100 });

        Assert.Equal(["big"], tiles.Select(t => t.Node.Name));
        Assert.Equal(10_000, tiles[0].Bounds.Area, 3); // the shown files share the whole map
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 100)]
    [InlineData(100, 0)]
    public void Layout_WithNoRoomOrNoBytes_IsEmpty(double width, double height)
    {
        var root = Folder(null, @"C:\");
        File(root, "a", 100);

        Assert.Empty(Treemap.Layout(root, new TileRect(0, 0, width, height)));
        Assert.Empty(Treemap.Layout(Folder(null, @"C:\"), new TileRect(0, 0, 100, 100)));
    }

    [Fact]
    public void Layout_OfASingleFile_IsOneTile()
    {
        var file = new SpaceNode("a.bin", false, 5, Now);

        var tile = Assert.Single(Treemap.Layout(file, new TileRect(0, 0, 10, 10)));

        Assert.Equal(file, tile.Node);
    }

    [Fact]
    public void HitTest_FindsTheTileUnderThePoint_AndTileOfFindsItBack()
    {
        var root = Folder(null, @"C:\");
        var big = File(root, "big", 900);
        var small = File(root, "small", 100);
        var tiles = Treemap.Layout(root, new TileRect(0, 0, 100, 100));
        var bigTile = tiles.Single(t => t.Node == big).Bounds;

        var hit = Treemap.HitTest(tiles, bigTile.X + 1, bigTile.Y + 1);

        Assert.Equal(big, hit!.Node);
        Assert.Equal(small, Treemap.TileOf(tiles, small)!.Node);
        Assert.Null(Treemap.HitTest(tiles, 500, 500));
    }

    [Fact]
    public void TileOf_AFileTooSmallToSee_IsTheRemainderTileOfAFolderAboveIt()
    {
        var root = Folder(null, @"C:\");
        File(root, "huge", 1_000_000);
        var tiny = Sub(root, "tiny");
        var inside = File(tiny, "x", 1);
        var tiles = Treemap.Layout(root, new TileRect(0, 0, 100, 100));

        var tile = Treemap.TileOf(tiles, inside)!;
        Assert.True(tile.IsRemainder);
        Assert.Equal(root, tile.Node);
    }

    [Theory]
    [InlineData("movie.MKV", 0)]
    [InlineData("song.flac", 1)]
    [InlineData("photo.jpg", 2)]
    [InlineData("report.pdf", 3)]
    [InlineData("backup.zip", 4)]
    [InlineData("main.cs", 5)]
    [InlineData("data.bin", 6)]
    [InlineData("noext", 6)]
    public void Colour_ByType(string name, int expected) =>
        Assert.Equal(expected, SpaceColors.CategoryOf(new SpaceNode(name, false, 1, Now), ColorMode.Type, Now));

    [Fact]
    public void Colour_ByType_AFolderBlockIsMixed() =>
        Assert.Equal(7, SpaceColors.CategoryOf(new SpaceNode("d", true, 1, Now), ColorMode.Type, Now));

    [Theory]
    [InlineData(5, 0)]
    [InlineData(100, 1)]
    [InlineData(300, 2)]
    [InlineData(500, 3)]
    [InlineData(2000, 4)]
    public void Colour_ByAge(int daysOld, int expected) =>
        Assert.Equal(expected, SpaceColors.CategoryOf(new SpaceNode("a", false, 1, Now.AddDays(-daysOld)), ColorMode.Age, Now));

    [Fact]
    public void Legend_ListsGroupsByTotalSize_WithoutEmptyOnes()
    {
        var root = Folder(null, @"C:\");
        File(root, "a.mkv", 900);
        File(root, "b.mkv", 100);
        File(root, "c.jpg", 500);

        var legend = SpaceColors.Legend(root, ColorMode.Type, Now);

        Assert.Equal([("Video", 1000L, 2), ("Pictures", 500L, 1)], legend.Select(l => (l.Label, l.Bytes, l.Files)));
    }

    [Fact]
    public void Legend_ByExtension_ListsTheBiggestAndGroupsTheRest()
    {
        var root = Folder(null, @"C:\");
        for (var i = 0; i < 6; i++)
        {
            File(root, $"f.ext{i}", 100 * (i + 1));
        }

        var legend = SpaceColors.Legend(root, ColorMode.Type, Now, byExtension: true, top: 3);

        Assert.Equal(["ext5", "ext4", "ext3", "other extensions"], legend.Select(l => l.Label));
        Assert.Equal(600, legend[^1].Bytes);
    }

    [Fact]
    public void Legend_ByAge_FindsStaleData()
    {
        var root = Folder(null, @"C:\");
        File(root, "old", 1000, Now.AddYears(-4));
        File(root, "new", 10, Now.AddDays(-1));

        var legend = SpaceColors.Legend(root, ColorMode.Age, Now);

        Assert.Equal("Over 2 years", legend[0].Label);
        Assert.Equal(1000, legend[0].Bytes);
    }
}
