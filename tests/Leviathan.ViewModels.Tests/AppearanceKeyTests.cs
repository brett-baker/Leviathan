using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

public sealed class AppearanceKeyTests
{
    private static AppearanceKey Key(string accent = "Blue", int iconSize = 16, bool mica = true) =>
        AppearanceKey.From(new AppearanceSettings { Accent = accent, IconSize = iconSize, Mica = mica });

    [Fact]
    public void AccentChangedFrom_NothingAppliedYet_IsTrue() => Assert.True(Key().AccentChangedFrom(null));

    [Fact]
    public void AccentChangedFrom_SameAccentOtherSize_IsFalse() => Assert.False(Key(iconSize: 24).AccentChangedFrom(Key()));

    [Fact]
    public void AccentChangedFrom_OtherAccent_IsTrue() => Assert.True(Key("Red").AccentChangedFrom(Key()));

    [Fact]
    public void Equality_AnyPartDiffers_KeysDiffer()
    {
        Assert.Equal(Key(), Key());
        Assert.NotEqual(Key(), Key(mica: false));
        Assert.NotEqual(Key(), Key(iconSize: 32));
    }
}
