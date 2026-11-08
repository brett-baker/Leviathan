using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Tests;

public sealed class ImageViewportTests
{
    private static ImageViewport Make(double iw, double ih, double vw, double vh)
    {
        var v = new ImageViewport();
        v.SetView(vw, vh);
        v.SetImage(iw, ih);
        return v;
    }

    [Fact]
    public void ALargePicture_StartsFittedAndCentred()
    {
        var v = Make(2000, 1000, 400, 400);

        Assert.True(v.IsFit);
        Assert.Equal(0.2, v.Scale, 6);
        Assert.Equal(0, v.OffsetX, 6);
        Assert.Equal(100, v.OffsetY, 6);
        Assert.Equal(20, v.Percent);
    }

    [Fact]
    public void ASmallPicture_IsNotEnlargedToFit()
    {
        var v = Make(100, 50, 400, 400);

        Assert.Equal(1, v.Scale);
        Assert.Equal(150, v.OffsetX, 6);
        Assert.Equal(175, v.OffsetY, 6);
    }

    [Fact]
    public void ResizingTheView_WhileFitted_RefitsThePicture()
    {
        var v = Make(2000, 1000, 400, 400);

        v.SetView(1000, 1000);

        Assert.True(v.IsFit);
        Assert.Equal(0.5, v.Scale, 6);
    }

    [Fact]
    public void ResizingTheView_WhileZoomed_KeepsTheZoom()
    {
        var v = Make(2000, 1000, 400, 400);
        v.ZoomAt(5, 200, 200);
        var scale = v.Scale;

        v.SetView(800, 800);

        Assert.False(v.IsFit);
        Assert.Equal(scale, v.Scale, 6);
    }

    [Fact]
    public void ZoomAt_KeepsThePictureUnderThePointerStill()
    {
        var v = Make(2000, 2000, 400, 400);
        v.ZoomAt(4, 100, 300); // 0.2 -> 0.8
        var before = ((100 - v.OffsetX) / v.Scale, (300 - v.OffsetY) / v.Scale);

        v.ZoomAt(2, 100, 300);

        Assert.Equal(before.Item1, (100 - v.OffsetX) / v.Scale, 4);
        Assert.Equal(before.Item2, (300 - v.OffsetY) / v.Scale, 4);
        Assert.False(v.IsFit);
    }

    [Fact]
    public void ZoomOut_CannotGoBelowFit()
    {
        var v = Make(2000, 1000, 400, 400);

        v.ZoomAt(0.01, 200, 200);

        Assert.Equal(0.2, v.Scale, 6);
        Assert.True(v.IsFit);
    }

    [Fact]
    public void ZoomIn_StopsAtTheMaximum()
    {
        var v = Make(2000, 1000, 400, 400);

        v.ZoomAt(1_000_000, 200, 200);

        Assert.Equal(ImageViewport.MaxScale, v.Scale);
    }

    [Fact]
    public void PanBy_CannotDragThePictureOffThePane()
    {
        var v = Make(2000, 1000, 400, 400);
        v.ZoomAt(10, 200, 200); // 200% -> picture 4000 x 2000

        v.PanBy(100_000, 100_000);

        Assert.Equal(0, v.OffsetX, 6);
        Assert.Equal(0, v.OffsetY, 6);

        v.PanBy(-100_000, -100_000);

        Assert.Equal(400 - 4000, v.OffsetX, 6);
        Assert.Equal(400 - 2000, v.OffsetY, 6);
    }

    [Fact]
    public void PanBy_OnAFittedPicture_LeavesItCentred()
    {
        var v = Make(2000, 1000, 400, 400);

        v.PanBy(50, 50);

        Assert.Equal(0, v.OffsetX, 6);
        Assert.Equal(100, v.OffsetY, 6);
    }

    [Fact]
    public void ToggleActualSize_GoesTo100Percent_ThenBackToFit()
    {
        var v = Make(2000, 1000, 400, 400);

        v.ToggleActualSize(200, 200);
        Assert.Equal(1, v.Scale, 6);
        Assert.False(v.IsFit);

        v.ToggleActualSize(200, 200);
        Assert.True(v.IsFit);
        Assert.Equal(0.2, v.Scale, 6);
    }

    [Fact]
    public void NoPicture_ChangesNothing()
    {
        var v = new ImageViewport();
        v.SetView(400, 400);

        v.ZoomAt(2, 10, 10);
        v.PanBy(5, 5);

        Assert.Equal(1, v.Scale);
    }

    [Fact]
    public void Fit_AfterZooming_ShowsTheWholePictureAgain()
    {
        var v = Make(2000, 1000, 400, 400);
        v.ZoomAt(8, 10, 10);

        v.Fit();

        Assert.True(v.IsFit);
        Assert.Equal(0.2, v.Scale, 6);
    }
}
