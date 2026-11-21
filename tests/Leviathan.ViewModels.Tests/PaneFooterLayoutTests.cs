using Leviathan.ViewModels.Panes;

namespace Leviathan.ViewModels.Tests;

public sealed class PaneFooterLayoutTests
{
    [Theory]
    [InlineData(2000, FooterParts.All)]
    [InlineData(730, FooterParts.All)]
    [InlineData(729, FooterParts.Health)]
    [InlineData(490, FooterParts.Health)]
    [InlineData(489, FooterParts.None)]
    [InlineData(0, FooterParts.None)]
    public void Visible_ByWidth_DropsFreeSpaceThenHealth(double width, FooterParts expected)
    {
        Assert.Equal(expected, PaneFooterLayout.Visible(width));
    }

    [Fact]
    public void Visible_NotMeasuredYet_ShowsEverything() => Assert.Equal(FooterParts.All, PaneFooterLayout.Visible(double.NaN));

    [Fact]
    public void Visible_AsThePaneShrinks_NeverBringsAPartBack()
    {
        var previous = FooterParts.All;
        for (var width = 1000.0; width >= 0; width -= 5)
        {
            var now = PaneFooterLayout.Visible(width);
            Assert.Equal(now, now & previous);
            previous = now;
        }
    }
}
