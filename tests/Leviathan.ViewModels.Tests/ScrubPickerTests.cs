using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Tests;

public sealed class ScrubPickerTests
{
    private static readonly double[] Seconds = [5, 15, 25, 35];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(11, 1)]
    [InlineData(29, 2)]
    [InlineData(1000, 3)]
    public void NearestFrame_PicksTheFrameTakenClosest(double at, int expected) => Assert.Equal(expected, ScrubPicker.NearestFrame(Seconds, at));

    [Fact]
    public void NearestFrame_OfNoFrames_IsMinusOne() => Assert.Equal(-1, ScrubPicker.NearestFrame([], 10));

    [Fact]
    public void TimeAt_ScalesTheFractionByTheDuration()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ScrubPicker.TimeAt(0.25, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.Zero, ScrubPicker.TimeAt(-1, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(120), ScrubPicker.TimeAt(7, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.Zero, ScrubPicker.TimeAt(double.NaN, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.Zero, ScrubPicker.TimeAt(0.5, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, 200, 0, 0)]
    [InlineData(100, 200, 0, 0.5)]
    [InlineData(200, 200, 0, 1)]
    [InlineData(-50, 200, 0, 0)]
    [InlineData(300, 200, 0, 1)]
    [InlineData(10, 200, 10, 0)]
    [InlineData(100, 200, 10, 0.5)]
    [InlineData(5, 0, 0, 0)]
    public void FractionAt_ClampsToTheTrack(double x, double width, double margin, double expected) => Assert.Equal(expected, ScrubPicker.FractionAt(x, width, margin), 6);

    [Fact]
    public void Spread_IncludesTheFirstAndLastFrame() => Assert.Equal([0, 6, 13, 19], ScrubPicker.Spread(20, 4));

    [Fact]
    public void Spread_NeverReturnsMoreThanThereAre() => Assert.Equal([0, 1, 2], ScrubPicker.Spread(3, 10));

    [Fact]
    public void Spread_OfOneSlot_IsTheMiddle() => Assert.Equal([10], ScrubPicker.Spread(20, 1));

    [Theory]
    [InlineData(0, 5)]
    [InlineData(20, 0)]
    public void Spread_OfNothing_IsEmpty(int frames, int fit) => Assert.Empty(ScrubPicker.Spread(frames, fit));
}
