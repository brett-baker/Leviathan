using System.IO.Abstractions.TestingHelpers;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

public sealed class RgbTests
{
    [Theory]
    [InlineData("#0F6CBD", 0x0F, 0x6C, 0xBD)]
    [InlineData("0f6cbd", 0x0F, 0x6C, 0xBD)]
    [InlineData("  #FFFFFF ", 255, 255, 255)]
    public void TryParse_ReadsAHexColour(string text, byte r, byte g, byte b)
    {
        Assert.True(Rgb.TryParse(text, out var color));
        Assert.Equal(new Rgb(r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("blue")]
    [InlineData("#0F6CBD00")]
    public void TryParse_RefusesAnythingElse(string? text) => Assert.False(Rgb.TryParse(text, out _));

    [Fact]
    public void Hex_IsUpperCase_AndRoundTrips()
    {
        Assert.Equal("#0F6CBD", new Rgb(0x0F, 0x6C, 0xBD).Hex);
        Assert.True(Rgb.TryParse(new Rgb(1, 2, 3).Hex, out var back));
        Assert.Equal(new Rgb(1, 2, 3), back);
    }

    [Fact]
    public void Toward_MovesPartOfTheWay()
    {
        Assert.Equal(new Rgb(100, 100, 100), new Rgb(0, 0, 0).Toward(new Rgb(200, 200, 200), 0.5));
        Assert.Equal(new Rgb(10, 20, 30), new Rgb(10, 20, 30).Toward(new Rgb(255, 255, 255), 0));
    }
}

public sealed class AccentPaletteTests
{
    [Fact]
    public void From_GivesLighterAndDarkerShades_InOrder()
    {
        var palette = AccentPalette.From("#0F6CBD")!;

        Assert.Equal(new Rgb(0x0F, 0x6C, 0xBD), palette.Accent);
        Assert.True(Brightness(palette.Light1) > Brightness(palette.Accent));
        Assert.True(Brightness(palette.Light2) > Brightness(palette.Light1));
        Assert.True(Brightness(palette.Light3) > Brightness(palette.Light2));
        Assert.True(Brightness(palette.Dark1) < Brightness(palette.Accent));
        Assert.True(Brightness(palette.Dark2) < Brightness(palette.Dark1));
        Assert.True(Brightness(palette.Dark3) < Brightness(palette.Dark2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a colour")]
    public void From_IsNullWhenThereIsNoColour_SoTheWindowsAccentStays(string? hex) => Assert.Null(AccentPalette.From(hex));

    [Fact]
    public void ALightTheme_UsesTheDarkerShades_AndADarkThemeTheLighterOnes()
    {
        var palette = AccentPalette.From("#0F6CBD")!;

        Assert.Equal(palette.Dark1, palette.FillForLight);
        Assert.Equal(palette.Light2, palette.FillForDark);
        Assert.Equal(palette.Dark2, palette.TextForLight);
        Assert.Equal(palette.Light3, palette.TextForDark);
    }

    [Fact]
    public void ThePresets_AreAllReadableColours_WithDistinctNames()
    {
        Assert.All(AccentPalette.Presets, p => Assert.NotNull(AccentPalette.From(p.Hex)));
        Assert.Equal(AccentPalette.Presets.Count, AccentPalette.Presets.Select(p => p.Name).Distinct().Count());
    }

    private static double Brightness(Rgb c) => (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
}

public sealed class AppearanceMetricsTests
{
    [Fact]
    public void Comfortable_IsTheRoomierRow_AndCompactTheTighter()
    {
        var comfortable = AppearanceMetrics.For(Density.Comfortable, 16);
        var compact = AppearanceMetrics.For(Density.Compact, 16);

        Assert.Equal(30, comfortable.DetailsRowHeight);
        Assert.Equal(24, compact.DetailsRowHeight);
        Assert.True(compact.SidebarRowPadding < comfortable.SidebarRowPadding);
    }

    [Theory]
    [InlineData(Density.Compact, 24, 30)]
    [InlineData(Density.Comfortable, 24, 32)]
    [InlineData(Density.Compact, 20, 26)]
    public void ARow_IsAlwaysTallEnoughForItsIcon(Density density, int icon, double expected)
    {
        var metrics = AppearanceMetrics.For(density, icon);

        Assert.Equal(expected, metrics.DetailsRowHeight);
        Assert.True(metrics.DetailsRowHeight >= metrics.IconSize + 4);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(17, 16)]
    [InlineData(19, 20)]
    [InlineData(22, 20)]
    [InlineData(23, 24)]
    [InlineData(400, 24)]
    public void TheIconSize_IsTheNearestOneOffered(int asked, int offered) => Assert.Equal(offered, AppearanceMetrics.NearestIconSize(asked));
}

public sealed class AppearanceSettingsTests
{
    [Fact]
    public void TheDefaults_AreWhatTheAppLooksLikeToday()
    {
        var appearance = new AppearanceSettings();

        Assert.Equal(ThemeMode.System, appearance.Theme);
        Assert.Equal(Density.Comfortable, appearance.Density);
        Assert.True(appearance.Mica);
        Assert.Equal(string.Empty, appearance.Accent);
        Assert.Equal(16, appearance.IconSize);
    }

    [Fact]
    public void Cleaned_RepairsWhatAHandEditedFileGotWrong_AndKeepsWhatIsRight()
    {
        var cleaned = new AppearanceSettings { Accent = "#0f6cbd", IconSize = 21, Theme = (ThemeMode)9, Density = (Density)9 }.Cleaned();

        Assert.Equal("#0F6CBD", cleaned.Accent);
        Assert.Equal(20, cleaned.IconSize);
        Assert.Equal(ThemeMode.System, cleaned.Theme);
        Assert.Equal(Density.Comfortable, cleaned.Density);
        Assert.Equal(string.Empty, new AppearanceSettings { Accent = "bluish" }.Cleaned().Accent);
    }

    [Fact]
    public async Task TheLook_IsSaved_AndComesBackAfterARestart()
    {
        var disk = new MockFileSystem();
        using var store = new SettingsStore(disk, @"C:\data\settings.json");
        await store.LoadAsync(CancellationToken.None);
        await store.UpdateAsync(s => s with { Appearance = s.Appearance with { Theme = ThemeMode.Dark, Density = Density.Compact, Mica = false, Accent = "#C42B1C", IconSize = 24 } }, CancellationToken.None);

        using var reopened = new SettingsStore(disk, @"C:\data\settings.json");
        var look = (await reopened.LoadAsync(CancellationToken.None)).Appearance;

        Assert.Equal(ThemeMode.Dark, look.Theme);
        Assert.Equal(Density.Compact, look.Density);
        Assert.False(look.Mica);
        Assert.Equal("#C42B1C", look.Accent);
        Assert.Equal(24, look.IconSize);
    }

    [Fact]
    public async Task AnOldFile_WithoutAnIconSize_GetsTheDefault()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\data\settings.json", new MockFileData("{ \"Appearance\": { \"Theme\": \"Light\" } }"));
        using var store = new SettingsStore(disk, @"C:\data\settings.json");

        var look = (await store.LoadAsync(CancellationToken.None)).Appearance;

        Assert.Equal(16, look.IconSize);
        Assert.Equal(ThemeMode.Light, look.Theme);
    }
}
