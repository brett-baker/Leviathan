namespace Leviathan.ViewModels.Settings;

/// <summary>
/// The parts of the appearance settings that make the app redraw. Comparing two keys decides whether anything needs applying, and
/// whether the (slower) accent colours do (spec §17.2 Appearance).
/// </summary>
public readonly record struct AppearanceKey(ThemeMode Theme, Density Density, bool Mica, string Accent, int IconSize)
{
    /// <summary>The key of <paramref name="look"/>.</summary>
    public static AppearanceKey From(AppearanceSettings look)
    {
        ArgumentNullException.ThrowIfNull(look);
        return new AppearanceKey(look.Theme, look.Density, look.Mica, look.Accent, look.IconSize);
    }

    /// <summary>True when the accent must be applied: nothing was applied before, or the accent differs.</summary>
    public bool AccentChangedFrom(AppearanceKey? previous) => previous is null || previous.Value.Accent != Accent;
}
