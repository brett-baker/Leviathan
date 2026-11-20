using Leviathan.ViewModels.Navigation;

namespace Leviathan.ViewModels.Tests;

public sealed class NavButtonLayoutTests
{
    [Theory]
    [InlineData(2000, NavButtons.All)]
    [InlineData(380, NavButtons.All)]
    [InlineData(379, NavButtons.Back | NavButtons.Forward | NavButtons.Up)]
    [InlineData(340, NavButtons.Back | NavButtons.Forward | NavButtons.Up)]
    [InlineData(300, NavButtons.Back | NavButtons.Forward)]
    [InlineData(260, NavButtons.Back)]
    [InlineData(100, NavButtons.Back)]
    [InlineData(0, NavButtons.Back)]
    public void Visible_ByWidth_HidesReloadThenUpThenForward_AndKeepsBack(double width, NavButtons expected)
    {
        Assert.Equal(expected, NavButtonLayout.Visible(width));
    }

    [Fact]
    public void Visible_NotMeasuredYet_ShowsEverything() => Assert.Equal(NavButtons.All, NavButtonLayout.Visible(double.NaN));

    [Fact]
    public void Visible_AsTheBarShrinks_NeverBringsAButtonBack()
    {
        var previous = NavButtons.All;
        for (var width = 800.0; width >= 0; width -= 5)
        {
            var now = NavButtonLayout.Visible(width);
            Assert.Equal(now, now & previous);
            previous = now;
        }
    }
}
