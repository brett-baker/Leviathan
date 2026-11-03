using Leviathan.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Leviathan.App;

/// <summary>
/// Gives the app its own accent colour (spec 17.2 Appearance) by putting the colours and brushes the controls take the accent from
/// into the application's resources, where they win over the ones of the system. An empty accent takes them out again, so the
/// Windows accent comes back. Controls read these through theme resources, so a window has to switch theme once to pick up a change
/// (<see cref="MainWindow"/> does).
/// </summary>
internal static class AccentResources
{
    private static readonly string[] ColorKeys =
    [
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    // The brushes the controls take the accent from live in the theme dictionaries WinUI merges into the application's resources; the
    // control templates look them up there first, so a copy placed beside them is not seen. They are replaced in place, and what was
    // there is kept so the Windows accent can be put back.
    private static readonly List<(ResourceDictionary Theme, string Key, object Original)> Replaced = [];

    /// <summary>Applies <paramref name="hex"/>, or restores the Windows accent for empty text.</summary>
    public static void Apply(string hex)
    {
        var resources = Application.Current.Resources;
        foreach (var (theme, key, original) in Replaced)
        {
            theme[key] = original;
        }

        Replaced.Clear();
        foreach (var key in ColorKeys)
        {
            resources.Remove(key);
        }

        if (AccentPalette.From(hex) is not { } palette)
        {
            return;
        }

        resources["SystemAccentColor"] = Color(palette.Accent);
        resources["SystemAccentColorLight1"] = Color(palette.Light1);
        resources["SystemAccentColorLight2"] = Color(palette.Light2);
        resources["SystemAccentColorLight3"] = Color(palette.Light3);
        resources["SystemAccentColorDark1"] = Color(palette.Dark1);
        resources["SystemAccentColorDark2"] = Color(palette.Dark2);
        resources["SystemAccentColorDark3"] = Color(palette.Dark3);
        Replace(resources, Brushes(palette.FillForLight, palette.TextForLight, palette.Accent), Brushes(palette.FillForDark, palette.TextForDark, palette.Accent));
        UiTrace.Mark($"accent {hex}: {Replaced.Count} theme brushes replaced, {resources.ThemeDictionaries.Count} own theme dictionaries, {resources.MergedDictionaries.Count} merged");
    }

    private static void Replace(ResourceDictionary dictionary, Dictionary<string, Brush> light, Dictionary<string, Brush> dark)
    {
        foreach (var (name, value) in dictionary.ThemeDictionaries)
        {
            if (value is not ResourceDictionary theme || name is "HighContrast")
            {
                continue;
            }

            foreach (var (key, brush) in (name is "Light" ? light : dark))
            {
                if (theme.TryGetValue(key, out var original))
                {
                    Replaced.Add((theme, key, original));
                    theme[key] = brush;
                }
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            Replace(merged, light, dark);
        }
    }

    private static Dictionary<string, Brush> Brushes(Rgb fill, Rgb text, Rgb accent) => new()
    {
        ["AccentFillColorDefaultBrush"] = Brush(fill, 1),
        ["AccentFillColorSecondaryBrush"] = Brush(fill, 0.9),
        ["AccentFillColorTertiaryBrush"] = Brush(fill, 0.8),
        ["AccentFillColorSelectedTextBackgroundBrush"] = Brush(accent, 1),
        ["AccentTextFillColorPrimaryBrush"] = Brush(text, 1),
        ["AccentTextFillColorSecondaryBrush"] = Brush(text, 1),
        ["SystemControlHighlightAccentBrush"] = Brush(fill, 1),
        ["SystemControlForegroundAccentBrush"] = Brush(text, 1),
    };

    private static SolidColorBrush Brush(Rgb color, double opacity) => new(Color(color)) { Opacity = opacity };

    private static Color Color(Rgb c) => Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
}
