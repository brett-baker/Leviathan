using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class ItemFlowTests
{
    [Theory]
    [InlineData(128, 1, 144)]
    [InlineData(128, -1, 112)]
    [InlineData(64, -3, 64)]
    [InlineData(512, 5, 512)]
    [InlineData(10, 0, 64)]
    [InlineData(9999, 0, 512)]
    public void Zoom_StepsAndStaysInsideTheRange(int start, int notches, int expected)
    {
        Assert.Equal(expected, ItemFlow.Zoom(start, notches));
    }

    [Fact]
    public void Zoom_EveryNotchMovesAtLeast8Pixels_AndReachesBothEnds()
    {
        var size = ItemFlow.MinThumbnailSize;
        for (var i = 0; i < 100 && size < ItemFlow.MaxThumbnailSize; i++)
        {
            var next = ItemFlow.Zoom(size, 1);
            Assert.True(next >= size + 8 || next == ItemFlow.MaxThumbnailSize);
            size = next;
        }

        Assert.Equal(ItemFlow.MaxThumbnailSize, size);
        Assert.Equal(ItemFlow.MinThumbnailSize, ItemFlow.Zoom(size, -100));
    }

    [Theory]
    [InlineData(1000, 100, 9)]
    [InlineData(100, 100, 1)]
    [InlineData(50, 100, 1)]
    [InlineData(0, 100, 1)]
    public void PerLine_FitsAsManyAsTheExtentAllows_AndNeverLessThanOne(double extent, double item, int expected)
    {
        Assert.Equal(expected, ItemFlow.PerLine(extent, item));
    }

    [Theory]
    [InlineData(5, FlowKey.Right, 6)]
    [InlineData(5, FlowKey.Left, 4)]
    [InlineData(5, FlowKey.Down, 9)]
    [InlineData(5, FlowKey.Up, 1)]
    [InlineData(0, FlowKey.Left, 0)]
    [InlineData(2, FlowKey.Up, 2)]
    [InlineData(18, FlowKey.Right, 18)]
    public void Neighbour_RowMajor_ArrowsMoveByOneAcrossAndByALineDown(int current, FlowKey key, int expected)
    {
        // 19 items, 4 per row: rows of 0-3, 4-7, 8-11, 12-15, 16-18.
        Assert.Equal(expected, ItemFlow.Neighbour(current, 19, key, 4, columnMajor: false));
    }

    [Fact]
    public void Neighbour_DownFromARowAboveAShortLastRow_LandsOnTheLastItem()
    {
        Assert.Equal(18, ItemFlow.Neighbour(15, 19, FlowKey.Down, 4, columnMajor: false));
        Assert.Equal(18, ItemFlow.Neighbour(18, 19, FlowKey.Down, 4, columnMajor: false));
    }

    [Theory]
    [InlineData(5, FlowKey.Down, 6)]
    [InlineData(5, FlowKey.Up, 4)]
    [InlineData(5, FlowKey.Right, 10)]
    [InlineData(5, FlowKey.Left, 0)]
    [InlineData(2, FlowKey.Left, 2)]
    public void Neighbour_ColumnMajor_ListFillsColumnsTopToBottom(int current, FlowKey key, int expected)
    {
        // 5 per column: columns of 0-4, 5-9, 10-14, 15-18.
        Assert.Equal(expected, ItemFlow.Neighbour(current, 19, key, 5, columnMajor: true));
    }

    [Fact]
    public void Neighbour_RightIntoAShortLastColumn_LandsOnTheLastItem()
    {
        Assert.Equal(18, ItemFlow.Neighbour(13, 19, FlowKey.Right, 5, columnMajor: true));
    }

    [Fact]
    public void Neighbour_WithNothingFocused_StartsAtTheFirstItem_AndEmptyListHasNone()
    {
        Assert.Equal(0, ItemFlow.Neighbour(-1, 5, FlowKey.Down, 2, columnMajor: false));
        Assert.Equal(-1, ItemFlow.Neighbour(-1, 0, FlowKey.Down, 2, columnMajor: false));
    }

    [Fact]
    public void PageDelta_IsAScreenfulOfLinesLessOne()
    {
        Assert.Equal(12, ItemFlow.PageDelta(4, 4));
        Assert.Equal(4, ItemFlow.PageDelta(1, 4));
    }

    [Fact]
    public void CellSize_GrowsWithTheThumbnail()
    {
        Assert.True(ItemFlow.CellWidth(256) > ItemFlow.CellWidth(128));
        Assert.True(ItemFlow.CellHeight(256) - ItemFlow.CellHeight(128) == 128);
    }

    [Fact]
    public void Tab_ZoomThumbnails_KeepsTheSizeInTheViewAndClamps()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), TabTestSupport.P(@"C:\a"));

        tab.SetLayout(ViewLayout.Thumbnails);
        tab.ZoomThumbnails(2);
        Assert.Equal(ItemFlow.Zoom(ItemFlow.DefaultThumbnailSize, 2), tab.ThumbnailSize);
        Assert.Equal(tab.ThumbnailSize, tab.CurrentView().ThumbnailSize);
        Assert.Equal(ViewLayout.Thumbnails, tab.CurrentView().Layout);

        tab.ZoomThumbnails(-50);
        Assert.Equal(ItemFlow.MinThumbnailSize, tab.ThumbnailSize);
    }

    [Fact]
    public void Tab_RestoredFromState_KeepsLayoutAndThumbnailSize()
    {
        var services = TabTestSupport.Services();
        var tab = new TabViewModel(services, TabTestSupport.P(@"C:\a"));
        tab.SetLayout(ViewLayout.Thumbnails);
        tab.ZoomThumbnails(3);

        var restored = TabViewModel.Restore(tab.CaptureState(), services);

        Assert.Equal(ViewLayout.Thumbnails, restored.Layout);
        Assert.Equal(tab.ThumbnailSize, restored.ThumbnailSize);
    }
}
