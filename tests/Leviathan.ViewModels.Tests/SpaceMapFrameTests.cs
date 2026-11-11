using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.SpaceMap;
using Leviathan.Testing;
using Leviathan.ViewModels.SpaceMap;

namespace Leviathan.ViewModels.Tests;

/// <summary>What the space map window draws from a scan (spec §14).</summary>
public sealed class SpaceMapFrameTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SpaceScanSession> ScanAsync()
    {
        var files = new FakeLocationProvider()
            .AddFile(@"D:\data\movies\a.mkv", 6_000_000)
            .AddFile(@"D:\data\movies\b.mkv", 3_000_000)
            .AddFile(@"D:\data\docs\report.pdf", 500_000)
            .AddFile(@"D:\data\docs\notes.txt", 100_000)
            .AddFile(@"D:\data\big.zip", 400_000);
        var session = new SpaceScanner(new LocationProviderRegistry([files]), null, new ManualTimeProvider()).Start(LocationPath.Parse(@"D:\data"));
        await session.Completion;
        return session;
    }

    private static SpaceMapState State(SpaceScanSession session, SpaceNode? zoom = null, ColorMode mode = ColorMode.Type, long min = 0, SpaceNode? selected = null, params SpaceNode[] expanded) =>
        new(zoom ?? session.Root, mode, min, selected, new HashSet<SpaceNode>(expanded));

    [Fact]
    public async Task Frame_HasATilePerFile_AndTheTreeOfTheZoomedFolder()
    {
        using var session = await ScanAsync();

        var frame = SpaceMapFrames.Build(session, State(session), 800, 600, Now);

        Assert.Equal(5, frame.Tiles.Count);
        Assert.Equal(["movies", "docs", "big.zip"], frame.Rows.Select(r => r.Name));
        Assert.Equal(@"Space map · D:\data", frame.Title);
    }

    [Fact]
    public async Task Rows_ShowSizeShareAndFileCount_AndOpenFoldersShowTheirChildren()
    {
        using var session = await ScanAsync();
        var movies = session.Root.Children.Single(c => c.Name == "movies");

        var frame = SpaceMapFrames.Build(session, State(session, expanded: movies), 800, 600, Now);

        Assert.Equal(["movies", "a.mkv", "b.mkv", "docs", "big.zip"], frame.Rows.Select(r => r.Name));
        var row = frame.Rows.Single(r => r.Name == "movies");
        Assert.True(row.IsExpanded);
        Assert.Equal("2", row.FilesText);
        Assert.Equal(9_000_000.0 / 10_000_000.0, row.Fraction, 3);
        Assert.Equal(0, row.Depth);
        Assert.Equal(1, frame.Rows.Single(r => r.Name == "a.mkv").Depth);
        Assert.Equal(6_000_000.0 / 9_000_000.0, frame.Rows.Single(r => r.Name == "a.mkv").Fraction, 3);
    }

    [Fact]
    public async Task Zoom_LaysOutOnlyThatFolder()
    {
        using var session = await ScanAsync();
        var movies = session.Root.Children.Single(c => c.Name == "movies");

        var frame = SpaceMapFrames.Build(session, State(session, movies), 800, 600, Now);

        Assert.Equal(2, frame.Tiles.Count);
        Assert.All(frame.Tiles, t => Assert.True(movies.Contains(t.Tile.Node)));
        Assert.Equal(@"Space map · D:\data\movies", frame.Title);
    }

    [Fact]
    public async Task ZoomingIntoANodeFromAnotherScan_FallsBackToTheRoot()
    {
        using var session = await ScanAsync();
        var stranger = new SpaceNode("x", true, 5, Now);

        var frame = SpaceMapFrames.Build(session, State(session, stranger), 800, 600, Now);

        Assert.Equal(5, frame.Tiles.Count);
    }

    [Fact]
    public async Task Tiles_AreColouredByType_OrByAge()
    {
        using var session = await ScanAsync();

        var byType = SpaceMapFrames.Build(session, State(session), 800, 600, Now);
        var byAge = SpaceMapFrames.Build(session, State(session, mode: ColorMode.Age), 800, 600, Now);

        Assert.Equal(0, byType.Tiles.Single(t => t.Tile.Node.Name == "a.mkv").Category);
        Assert.Equal(4, byType.Tiles.Single(t => t.Tile.Node.Name == "big.zip").Category);
        Assert.All(byAge.Tiles, t => Assert.InRange(t.Category, 0, 4));
        Assert.Equal("Video", byType.Legend[0].Label);
    }

    [Fact]
    public async Task SizeFilter_HidesSmallFilesFromTheMap_ButNotFromTheTree()
    {
        using var session = await ScanAsync();

        var frame = SpaceMapFrames.Build(session, State(session, min: 1_000_000), 800, 600, Now);

        Assert.Equal(["a.mkv", "b.mkv"], frame.Tiles.Select(t => t.Tile.Node.Name).Order());
        Assert.Equal(3, frame.Rows.Count);
    }

    [Fact]
    public async Task NodeAt_FindsTheFileUnderAClick_AndTileOfFindsItBack()
    {
        using var session = await ScanAsync();
        var frame = SpaceMapFrames.Build(session, State(session), 800, 600, Now);
        var big = frame.Tiles.Single(t => t.Tile.Node.Name == "a.mkv").Tile;

        var hit = SpaceMapFrames.NodeAt(frame, big.Bounds.X + 2, big.Bounds.Y + 2);

        Assert.Equal("a.mkv", hit!.Name);
        Assert.Equal(big, SpaceMapFrames.TileOf(frame, hit));
        Assert.Null(SpaceMapFrames.NodeAt(frame, -5, -5));
    }

    [Fact]
    public async Task Status_SaysHowMuchWasFound_AndWhatIsSelected()
    {
        using var session = await ScanAsync();
        var movies = session.Root.Children.Single(c => c.Name == "movies");

        var frame = SpaceMapFrames.Build(session, State(session, selected: movies), 800, 600, Now);

        Assert.Contains("in 5 files", frame.Status, StringComparison.Ordinal);
        Assert.Contains(@"D:\data\movies:", frame.Status, StringComparison.Ordinal);
        Assert.True(frame.Rows.Single(r => r.Name == "movies").IsSelected);
    }

    [Fact]
    public async Task Status_WhileAScanIsPaused_SaysSo()
    {
        var files = new FakeLocationProvider();
        for (var i = 0; i < 200; i++)
        {
            files.AddFile($@"D:\data\f{i}\x.bin", 10);
        }

        using var session = new SpaceScanner(new LocationProviderRegistry([files]), null, new ManualTimeProvider(), new SpaceScanOptions { Parallelism = 1 }).Start(LocationPath.Parse(@"D:\data"));
        session.Pause();
        await Task.Delay(50);

        var frame = SpaceMapFrames.Build(session, State(session), 400, 300, Now);

        Assert.StartsWith("Paused", frame.Status, StringComparison.Ordinal);
        session.Resume();
        await session.Completion;
    }

    [Fact]
    public async Task Rows_LongFolders_AreCappedWithALineSayingHowManyAreLeft()
    {
        var files = new FakeLocationProvider();
        for (var i = 0; i < SpaceMapFrames.RowsPerFolder + 25; i++)
        {
            files.AddFile($@"D:\data\f{i:D4}.bin", 100 + i);
        }

        using var session = new SpaceScanner(new LocationProviderRegistry([files]), null, new ManualTimeProvider()).Start(LocationPath.Parse(@"D:\data"));
        await session.Completion;

        var frame = SpaceMapFrames.Build(session, State(session), 800, 600, Now);

        Assert.Equal(SpaceMapFrames.RowsPerFolder + 1, frame.Rows.Count);
        Assert.Contains("25 smaller items", frame.Rows[^1].Name, StringComparison.Ordinal);
    }
}
