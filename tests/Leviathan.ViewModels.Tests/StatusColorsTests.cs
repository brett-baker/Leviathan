using Leviathan.Core.Git;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

/// <summary>Meaningful colours stay readable in both themes (WCAG 1.4.3, at least 4.5 to 1; build plan G4).</summary>
public sealed class StatusColorsTests
{
    [Fact]
    public void Contrast_OfBlackOnWhite_Is21_AndOfAColourWithItself_Is1()
    {
        Assert.Equal(21.0, StatusColors.Contrast(new Rgb(0, 0, 0), new Rgb(255, 255, 255)), 3);
        Assert.Equal(1.0, StatusColors.Contrast(new Rgb(0x80, 0x80, 0x80), new Rgb(0x80, 0x80, 0x80)), 3);
    }

    public static TheoryData<GitState, bool> GitStatesInBothThemes()
    {
        var data = new TheoryData<GitState, bool>();
        foreach (var state in Enum.GetValues<GitState>())
        {
            data.Add(state, false);
            data.Add(state, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GitStatesInBothThemes))]
    public void GitStateColour_IsReadableOnEveryBackgroundOfItsTheme(GitState state, bool dark)
    {
        var colour = StatusColors.Git(state, dark);
        foreach (var background in StatusColors.Backgrounds(dark))
        {
            var ratio = StatusColors.Contrast(colour, background);
            Assert.True(ratio >= 4.5, $"{state} {colour.Hex} on {background.Hex} ({(dark ? "dark" : "light")}) is {ratio:0.00} to 1");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ErrorAndWarningColours_AreReadableInBothThemes(bool dark)
    {
        foreach (var colour in new[] { StatusColors.Failed(dark), StatusColors.Warning(dark) })
        {
            foreach (var background in StatusColors.Backgrounds(dark))
            {
                var ratio = StatusColors.Contrast(colour, background);
                Assert.True(ratio >= 4.5, $"{colour.Hex} on {background.Hex} ({(dark ? "dark" : "light")}) is {ratio:0.00} to 1");
            }
        }
    }

    [Fact]
    public void DeletedAndAdded_AreDifferentColours_InBothThemes()
    {
        // Colour is never the only signal: the state is also a letter and a tooltip.
        foreach (var dark in new[] { false, true })
        {
            var added = StatusColors.Git(GitState.Added, dark);
            var deleted = StatusColors.Git(GitState.Deleted, dark);
            Assert.NotEqual(added, deleted);
        }
    }
}
