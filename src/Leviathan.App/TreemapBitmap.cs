using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.SpaceMap;
using Leviathan.ViewModels.SpaceMap;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leviathan.App;

/// <summary>
/// Draws the treemap of the space map (spec §14) into a bitmap with plain arithmetic: a window of a million files is a few thousand rectangles, and filling
/// them into a pixel buffer is quicker and lighter than one XAML element for each. Colours come from small palettes (by kind, by age) that read in light and dark.
/// </summary>
internal static class TreemapBitmap
{
    private static readonly uint[] TypeColours = [0x3B82F6, 0xA855F7, 0x22C55E, 0xF59E0B, 0xEF4444, 0x14B8A6, 0x94A3B8, 0x64748B];
    private static readonly uint[] AgeColours = [0x22C55E, 0x84CC16, 0xEAB308, 0xF97316, 0xEF4444];

    /// <summary>The colour of a group as red, green and blue.</summary>
    public static (byte R, byte G, byte B) ColourOf(ColorMode mode, int category)
    {
        var palette = mode == ColorMode.Type ? TypeColours : AgeColours;
        var value = palette[Math.Clamp(category, 0, palette.Length - 1)];
        return ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>Draws <paramref name="frame"/> into a new bitmap of the given size, with <paramref name="selected"/> outlined.</summary>
    public static WriteableBitmap Render(SpaceMapFrame frame, ColorMode mode, int width, int height, bool dark, TreemapTile? selected) =>
        Render(frame.Tiles, mode, width, height, dark, selected);

    /// <summary>Draws <paramref name="tiles"/> into a new bitmap of the given size, with <paramref name="selected"/> outlined.</summary>
    public static WriteableBitmap Render(IReadOnlyList<MapTile> tiles, ColorMode mode, int width, int height, bool dark, TreemapTile? selected)
    {
        var bitmap = new WriteableBitmap(width, height);
        var pixels = new byte[width * height * 4];
        var background = dark ? (byte)0x20 : (byte)0xF3;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = background;
            pixels[i + 3] = 255;
        }

        foreach (var mapTile in tiles)
        {
            Fill(pixels, width, height, mapTile.Tile.Bounds, ColourOf(mode, mapTile.Category), border: true);
        }

        if (selected is not null)
        {
            Outline(pixels, width, height, selected.Bounds, dark);
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    private static void Fill(byte[] pixels, int width, int height, TileRect rect, (byte R, byte G, byte B) colour, bool border)
    {
        var x0 = Math.Clamp((int)Math.Round(rect.X), 0, width);
        var y0 = Math.Clamp((int)Math.Round(rect.Y), 0, height);
        var x1 = Math.Clamp((int)Math.Round(rect.Right), 0, width);
        var y1 = Math.Clamp((int)Math.Round(rect.Bottom), 0, height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        // A thin darker edge on the right and bottom of a tile big enough to have one separates neighbours of the same colour.
        var edge = border && x1 - x0 >= 4 && y1 - y0 >= 4;
        var dark = ((byte)(colour.R * 0.72), (byte)(colour.G * 0.72), (byte)(colour.B * 0.72));
        for (var y = y0; y < y1; y++)
        {
            var row = y * width * 4;
            for (var x = x0; x < x1; x++)
            {
                var useDark = edge && (x == x1 - 1 || y == y1 - 1);
                var i = row + (x * 4);
                pixels[i] = useDark ? dark.Item3 : colour.B;
                pixels[i + 1] = useDark ? dark.Item2 : colour.G;
                pixels[i + 2] = useDark ? dark.Item1 : colour.R;
                pixels[i + 3] = 255;
            }
        }
    }

    private static void Outline(byte[] pixels, int width, int height, TileRect rect, bool dark)
    {
        var x0 = Math.Clamp((int)Math.Round(rect.X), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Round(rect.Y), 0, height - 1);
        var x1 = Math.Clamp((int)Math.Round(rect.Right) - 1, 0, width - 1);
        var y1 = Math.Clamp((int)Math.Round(rect.Bottom) - 1, 0, height - 1);
        var inner = dark ? (byte)255 : (byte)0;
        var outer = dark ? (byte)0 : (byte)255;
        for (var t = 0; t < 2; t++)
        {
            var value = t == 0 ? inner : outer;
            for (var x = x0; x <= x1; x++)
            {
                Put(pixels, width, height, x, y0 + t, value);
                Put(pixels, width, height, x, y1 - t, value);
            }

            for (var y = y0; y <= y1; y++)
            {
                Put(pixels, width, height, x0 + t, y, value);
                Put(pixels, width, height, x1 - t, y, value);
            }
        }
    }

    private static void Put(byte[] pixels, int width, int height, int x, int y, byte value)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var i = ((y * width) + x) * 4;
        pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
        pixels[i + 3] = 255;
    }
}
