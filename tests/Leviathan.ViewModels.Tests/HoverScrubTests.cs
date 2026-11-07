using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

public sealed class HoverScrubTests
{
    [Theory]
    [InlineData(0, 200, 0)]
    [InlineData(100, 200, 0.5)]
    [InlineData(200, 200, 1)]
    [InlineData(-30, 200, 0)]
    [InlineData(500, 200, 1)]
    [InlineData(10, 0, 0)]
    public void Fraction_IsThePositionAcrossThePicture_KeptInRange(double x, double width, double expected) =>
        Assert.Equal(expected, HoverScrub.Fraction(x, width));

    [Fact]
    public void FrameAt_SpreadsTheWidthEvenlyOverTheFrames_AndNeverLeavesTheStrip()
    {
        Assert.Equal(0, HoverScrub.FrameAt(0, 20));
        Assert.Equal(9, HoverScrub.FrameAt(0.499, 20));
        Assert.Equal(10, HoverScrub.FrameAt(0.5, 20));
        Assert.Equal(19, HoverScrub.FrameAt(1, 20));
        Assert.Equal(19, HoverScrub.FrameAt(7, 20));
        Assert.Equal(0, HoverScrub.FrameAt(-1, 20));
        Assert.Equal(0, HoverScrub.FrameAt(0.5, 0));
    }

    [Fact]
    public void Place_AWideFrameInASquareBox_IsCentredAndClippedToTheFrame()
    {
        // 16:9 frames in a 160 x 160 box: 160 wide, 90 tall, 35 down.
        var place = HoverScrub.Place(frame: 3, frames: 20, cellWidth: 160, cellHeight: 90, boxWidth: 160, boxHeight: 160);

        Assert.Equal((0, 35, 160, 90), (place.ClipLeft, place.ClipTop, place.ClipWidth, place.ClipHeight));
        Assert.Equal(-3 * 160, place.StripLeft);
        Assert.Equal((20 * 160, 90), (place.StripWidth, place.StripHeight));
    }

    [Fact]
    public void Place_ATallFrameInASquareBox_FitsTheHeight_AndIsCentredAcross()
    {
        var place = HoverScrub.Place(frame: 0, frames: 16, cellWidth: 90, cellHeight: 160, boxWidth: 160, boxHeight: 160);

        Assert.Equal((35, 0, 90, 160), (place.ClipLeft, place.ClipTop, place.ClipWidth, place.ClipHeight));
        Assert.Equal(35, place.StripLeft);
    }

    [Fact]
    public void Place_WithoutASizeOrFrames_IsEmpty() =>
        Assert.Equal(default, HoverScrub.Place(0, 0, 160, 90, 160, 160));
}
