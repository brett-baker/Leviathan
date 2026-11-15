using Leviathan.Core.Git;

namespace Leviathan.ViewModels.Settings;

/// <summary>
/// Colours that carry meaning in text (a Git state letter, a failed checksum) chosen for the theme they sit on, so each reads at
/// least 4.5 to 1 against the window and row backgrounds of both themes (WCAG 1.4.3; accessibility pass, build plan G4). A colour
/// that suits a white window is unreadable on a dark one, so there is one set per theme. The words of the state are always given too
/// (tooltip, text), so colour is never the only signal.
/// </summary>
public static class StatusColors
{
    /// <summary>The colour of a Git state letter on the light or dark theme; grey for states with no colour of their own.</summary>
    /// <param name="state">The state.</param>
    /// <param name="dark">True on the dark theme.</param>
    public static Rgb Git(GitState state, bool dark) => state switch
    {
        GitState.Modified => dark ? new Rgb(0xF2, 0xA1, 0x54) : new Rgb(0xA8, 0x4B, 0x00),
        GitState.Added => dark ? new Rgb(0x6C, 0xCB, 0x7C) : new Rgb(0x1F, 0x7A, 0x30),
        GitState.Deleted or GitState.Conflicted => Failed(dark),
        GitState.Renamed => dark ? new Rgb(0x6C, 0xB6, 0xFF) : new Rgb(0x00, 0x58, 0xA8),
        GitState.Untracked => dark ? new Rgb(0xB9, 0xA6, 0xFF) : new Rgb(0x5B, 0x3F, 0xA8),
        _ => dark ? new Rgb(0xB0, 0xB0, 0xB0) : new Rgb(0x59, 0x59, 0x59),
    };

    /// <summary>The colour of an error in text (a mismatch, a failure).</summary>
    public static Rgb Failed(bool dark) => dark ? new Rgb(0xFF, 0x8F, 0x94) : new Rgb(0xB3, 0x26, 0x1E);

    /// <summary>The colour of a warning in text (a file that is missing).</summary>
    public static Rgb Warning(bool dark) => dark ? new Rgb(0xFF, 0xD0, 0x60) : new Rgb(0x8A, 0x5A, 0x00);

    /// <summary>The backgrounds text must stay readable on: the window and the alternate row of each theme.</summary>
    public static IReadOnlyList<Rgb> Backgrounds(bool dark) => dark
        ? [new Rgb(0x20, 0x20, 0x20), new Rgb(0x2B, 0x2B, 0x2B)]
        : [new Rgb(0xFF, 0xFF, 0xFF), new Rgb(0xF3, 0xF3, 0xF3)];

    /// <summary>The WCAG contrast ratio of two colours, from 1 (identical) to 21 (black on white).</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var x = Luminance(a);
        var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(Rgb c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
