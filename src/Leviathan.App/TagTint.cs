using Leviathan.ViewModels.Tags;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Leviathan.App;

/// <summary>The faint wash behind a tagged file's row or cell (spec §11.1), when the Tags.TintRows setting is on.</summary>
internal static class TagTint
{
    private const byte Alpha = 0x26;
    private static readonly Dictionary<string, Brush> s_brushes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when tinting is switched on (the views then also read tags for rows without a Tags column).</summary>
    public static bool Enabled => App.Host.Settings.Current.Tags.TintRows;

    /// <summary>The wash for a file with these tags (the first one's colour), or null for none.</summary>
    public static Brush? For(IReadOnlyList<ColorTag> tags)
    {
        if (tags.Count == 0 || !Enabled)
        {
            return null;
        }

        var hex = tags[0].Colour;
        if (!s_brushes.TryGetValue(hex, out var brush))
        {
            var colour = GridRow.TagColour(hex);
            brush = new SolidColorBrush(Color.FromArgb(Alpha, colour.R, colour.G, colour.B));
            s_brushes[hex] = brush;
        }

        return brush;
    }
}
