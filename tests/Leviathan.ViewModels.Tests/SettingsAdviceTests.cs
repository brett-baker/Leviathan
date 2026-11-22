using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

public sealed class SettingsAdviceTests
{
    [Theory]
    [InlineData("Ctrl+Shift+O", new[] { "Ctrl", "Shift", "O" })]
    [InlineData("F2", new[] { "F2" })]
    [InlineData("Ctrl++", new[] { "Ctrl", "+" })]
    [InlineData("Alt+Left", new[] { "Alt", "←" })]
    [InlineData("+", new[] { "+" })]
    [InlineData("", new string[0])]
    public void KeyCaps_Split_GivesOneCapPerKey(string keys, string[] expected)
    {
        Assert.Equal(expected, KeyCaps.Split(keys).Select(c => c.Text));
    }

    [Fact]
    public void KeyCaps_Split_PutsOrBetweenShortcuts()
    {
        var caps = KeyCaps.Split("Delete, Ctrl+D");
        Assert.Equal(["Delete", "or", "Ctrl", "D"], caps.Select(c => c.Text));
        Assert.Equal([true, false, true, true], caps.Select(c => c.IsCap));
    }

    [Fact]
    public void KeyScopes_Describe_SaysTriageKeysWorkOnlyInTriage()
    {
        Assert.Contains("triage window", KeyScopes.Describe("Triage · Decide"));
        Assert.Contains("file window", KeyScopes.Describe("Edit"));
    }

    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(8, 4, 2)]
    [InlineData(32, 6, 4)]
    public void OperationsAdvice_ScalesWithCoresWithinBounds(int cores, int running, int background)
    {
        Assert.Equal(running, OperationsAdvice.RunningJobs(cores).Value);
        Assert.Equal(background, OperationsAdvice.Background(cores).Value);
        Assert.Equal(1, OperationsAdvice.PerDevice().Value);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(40, 40)]
    [InlineData(900, 100)]
    public void TriageVolume_IsKeptBetweenZeroAndAHundred(int saved, int expected)
    {
        Assert.Equal(expected, new MediaSettings { TriageVolumePercent = saved }.Clamped().TriageVolumePercent);
    }

    [Fact]
    public void Pages_AllHaveAnIcon()
    {
        Assert.All(SettingsCatalog.Pages, p => Assert.False(string.IsNullOrEmpty(p.Glyph)));
    }
}
