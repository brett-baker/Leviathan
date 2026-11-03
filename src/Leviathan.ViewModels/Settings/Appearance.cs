using System.Globalization;

namespace Leviathan.ViewModels.Settings;

/// <summary>A colour as three bytes.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>The colour as <c>#RRGGBB</c>.</summary>
    public string Hex => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>Reads <c>#RRGGBB</c> (the <c>#</c> optional); false for anything else.</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>This colour moved <paramref name="amount"/> (0 to 1) of the way towards <paramref name="other"/>.</summary>
    public Rgb Toward(Rgb other, double amount) => new(
        (byte)Math.Round(R + ((other.R - R) * amount)),
        (byte)Math.Round(G + ((other.G - G) * amount)),
        (byte)Math.Round(B + ((other.B - B) * amount)));
}

/// <summary>
/// The shades the controls take from one accent colour (spec §17.2 Appearance: accent). Windows works out Light1 to Light3 and Dark1
/// to Dark3 from the accent the user picked in its settings; when the app has its own accent it has to do the same.
/// </summary>
/// <param name="Accent">The colour itself.</param>
/// <param name="Light1">A little lighter.</param>
/// <param name="Light2">Lighter.</param>
/// <param name="Light3">Much lighter.</param>
/// <param name="Dark1">A little darker.</param>
/// <param name="Dark2">Darker.</param>
/// <param name="Dark3">Much darker.</param>
public sealed record AccentPalette(Rgb Accent, Rgb Light1, Rgb Light2, Rgb Light3, Rgb Dark1, Rgb Dark2, Rgb Dark3)
{
    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb Black = new(0, 0, 0);

    /// <summary>The accent colours offered by name, besides "Windows accent" (no colour of its own).</summary>
    public static IReadOnlyList<(string Name, string Hex)> Presets { get; } =
    [
        ("Blue", "#0F6CBD"),
        ("Teal", "#0F7B8B"),
        ("Green", "#107C10"),
        ("Orange", "#CA5010"),
        ("Red", "#C42B1C"),
        ("Pink", "#C239B3"),
        ("Purple", "#8764B8"),
        ("Gray", "#5D6B7A"),
    ];

    /// <summary>The palette of <paramref name="hex"/>, or null for empty or unreadable text (then the Windows accent stays).</summary>
    public static AccentPalette? From(string? hex)
    {
        if (!Rgb.TryParse(hex, out var accent))
        {
            return null;
        }

        return new AccentPalette(
            accent,
            accent.Toward(White, 0.20),
            accent.Toward(White, 0.40),
            accent.Toward(White, 0.60),
            accent.Toward(Black, 0.15),
            accent.Toward(Black, 0.30),
            accent.Toward(Black, 0.45));
    }

    /// <summary>The colour fills and highlights use on a light theme (darker, so white text on it reads).</summary>
    public Rgb FillForLight => Dark1;

    /// <summary>The colour fills and highlights use on a dark theme (lighter, so it stands out from the background).</summary>
    public Rgb FillForDark => Light2;

    /// <summary>The colour of accent-coloured text on a light theme.</summary>
    public Rgb TextForLight => Dark2;

    /// <summary>The colour of accent-coloured text on a dark theme.</summary>
    public Rgb TextForDark => Light3;
}

/// <summary>The sizes a density and an icon size mean (spec §17.2 Appearance, D18).</summary>
/// <param name="DetailsRowHeight">Height of a row of the Details view.</param>
/// <param name="SidebarRowPadding">Space above and below a sidebar row's text.</param>
/// <param name="IconSize">Size of the icons in the file list.</param>
public sealed record AppearanceMetrics(double DetailsRowHeight, double SidebarRowPadding, double IconSize)
{
    /// <summary>The icon sizes the page offers.</summary>
    public static IReadOnlyList<int> IconSizes { get; } = [16, 20, 24];

    /// <summary>The size nearest to <paramref name="size"/> among <see cref="IconSizes"/> (a hand-edited file cannot ask for 400).</summary>
    public static int NearestIconSize(int size) => IconSizes.MinBy(s => Math.Abs(s - size));

    /// <summary>The sizes for a density and an icon size. A row is always tall enough for its icon.</summary>
    public static AppearanceMetrics For(Density density, int iconSize)
    {
        var icon = NearestIconSize(iconSize);
        var row = density == Density.Compact ? 24d : 30d;
        return new AppearanceMetrics(Math.Max(row, icon + (density == Density.Compact ? 6 : 8)), density == Density.Compact ? 1 : 3, icon);
    }
}
