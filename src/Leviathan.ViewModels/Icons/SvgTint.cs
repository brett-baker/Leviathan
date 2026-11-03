using System.Text.RegularExpressions;

namespace Leviathan.ViewModels.Icons;

/// <summary>
/// Gives a single-colour SVG the colour it should have (spec §4.3). Packs ship icons in one fixed colour (Fluent: #212121, Font
/// Awesome: the default black), which would vanish on a dark theme, so the app recolours them for the role and the theme. Colour
/// icons are never passed through here.
/// </summary>
public static partial class SvgTint
{
    /// <summary>
    /// Returns <paramref name="svg"/> with every fixed fill colour replaced by <paramref name="color"/> (<c>#RRGGBB</c>), and the
    /// colour set on the root when the file does not set one (Font Awesome). <c>fill="none"</c> and gradient references are kept.
    /// </summary>
    public static string Apply(string svg, string color)
    {
        ArgumentNullException.ThrowIfNull(svg);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        var tinted = FixedFill().Replace(svg, $"fill=\"{color}\"");
        var rootEnd = tinted.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (rootEnd < 0)
        {
            return tinted;
        }

        var tagEnd = tinted.IndexOf('>', rootEnd);
        if (tagEnd < 0)
        {
            return tinted;
        }

        var root = tinted.AsSpan(rootEnd, tagEnd - rootEnd);
        return root.Contains("fill=", StringComparison.Ordinal)
            ? tinted
            : string.Concat(tinted.AsSpan(0, rootEnd + 4), $" fill=\"{color}\"", tinted.AsSpan(rootEnd + 4));
    }

    [GeneratedRegex("fill=\"#[0-9a-fA-F]{3,8}\"")]
    private static partial Regex FixedFill();
}
