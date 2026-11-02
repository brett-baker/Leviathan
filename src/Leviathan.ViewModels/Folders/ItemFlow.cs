using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Folders;

/// <summary>An arrow key pressed in a wrapping layout.</summary>
public enum FlowKey
{
    /// <summary>Left arrow.</summary>
    Left,

    /// <summary>Right arrow.</summary>
    Right,

    /// <summary>Up arrow.</summary>
    Up,

    /// <summary>Down arrow.</summary>
    Down,
}

/// <summary>
/// Geometry and keyboard navigation of the wrapping layouts (spec §8.1): Thumbnails fill rows left to right and scroll
/// vertically; List fills columns top to bottom and scrolls horizontally. Kept free of UI types so the rules are unit-tested.
/// </summary>
public static class ItemFlow
{
    /// <summary>Smallest thumbnail edge (Ctrl+wheel, spec §8.1).</summary>
    public const int MinThumbnailSize = 64;

    /// <summary>Largest thumbnail edge.</summary>
    public const int MaxThumbnailSize = 512;

    /// <summary>Thumbnail edge when a tab has not chosen one.</summary>
    public const int DefaultThumbnailSize = 128;

    /// <summary>Row height of the List layout.</summary>
    public const double ListItemHeight = 28;

    /// <summary>Column width of the List layout.</summary>
    public const double ListItemWidth = 240;

    /// <summary>Space between items.</summary>
    public const double Spacing = 4;

    /// <summary>Height of the name under a thumbnail (two lines).</summary>
    public const double ThumbnailCaptionHeight = 40;

    /// <summary>Padding around a thumbnail cell.</summary>
    public const double ThumbnailPadding = 8;

    /// <summary>True for layouts that fill columns top to bottom (List).</summary>
    public static bool IsColumnMajor(ViewLayout layout) => layout == ViewLayout.List;

    /// <summary>True for the layouts this control draws.</summary>
    public static bool IsFlow(ViewLayout layout) => layout is ViewLayout.List or ViewLayout.Thumbnails;

    /// <summary>Width of a Thumbnails cell for a thumbnail edge.</summary>
    public static double CellWidth(int thumbnailSize) => thumbnailSize + (2 * ThumbnailPadding);

    /// <summary>Height of a Thumbnails cell for a thumbnail edge.</summary>
    public static double CellHeight(int thumbnailSize) => thumbnailSize + ThumbnailCaptionHeight + (2 * ThumbnailPadding);

    /// <summary>Keeps a thumbnail edge inside 64 to 512 pixels.</summary>
    public static int ClampThumbnailSize(int size) => Math.Clamp(size, MinThumbnailSize, MaxThumbnailSize);

    /// <summary>
    /// The thumbnail edge after <paramref name="notches"/> mouse-wheel notches (positive = bigger). Each notch changes it by
    /// about an eighth, rounded to 8 px, and always by at least 8 px so a notch is never lost at the ends of the range.
    /// </summary>
    public static int Zoom(int size, int notches)
    {
        var current = ClampThumbnailSize(size);
        for (var i = 0; i < Math.Abs(notches); i++)
        {
            var step = Math.Max(8, (int)Math.Round(current / 8.0 / 8.0) * 8);
            current = ClampThumbnailSize(current + (notches > 0 ? step : -step));
        }

        return current;
    }

    /// <summary>
    /// Items per line: per row for Thumbnails, per column for List. <paramref name="crossExtent"/> is the viewport width for
    /// Thumbnails and its height for List.
    /// </summary>
    public static int PerLine(double crossExtent, double itemCross) =>
        Math.Max(1, (int)Math.Floor((crossExtent + Spacing) / (itemCross + Spacing)));

    /// <summary>
    /// The item an arrow key moves the focus to, or <paramref name="current"/> when there is nowhere to go. Moving past the end
    /// of a short last line lands on the last item (as in Explorer) rather than doing nothing.
    /// </summary>
    public static int Neighbour(int current, int count, FlowKey key, int perLine, bool columnMajor)
    {
        if (count <= 0)
        {
            return -1;
        }

        if (current < 0)
        {
            return 0;
        }

        // Along a line the step is one item; across lines it is a whole line.
        var along = columnMajor ? key is FlowKey.Up or FlowKey.Down : key is FlowKey.Left or FlowKey.Right;
        var forward = key is FlowKey.Right or FlowKey.Down;
        var step = along ? 1 : perLine;
        var target = forward ? current + step : current - step;
        if (target < 0)
        {
            return current;
        }

        if (target >= count)
        {
            // Across lines: go to the last item when this is not already the last line.
            return !along && current / perLine < (count - 1) / perLine ? count - 1 : current;
        }

        return target;
    }

    /// <summary>
    /// Indexes of the items whose cells touch a rubber band (spec §8.4). The band is in the coordinates of the item area, whose
    /// first cell starts at 0,0. Only the cells under the band are visited, so a band over a 100,000-item folder is cheap.
    /// </summary>
    public static IEnumerable<int> IndexesIn(double left, double top, double right, double bottom, int count, double itemWidth, double itemHeight, int perLine, bool columnMajor)
    {
        var stepX = itemWidth + Spacing;
        var stepY = itemHeight + Spacing;
        var firstX = Math.Max(0, (int)Math.Ceiling((left - itemWidth) / stepX));
        var lastX = (int)Math.Floor(right / stepX);
        var firstY = Math.Max(0, (int)Math.Ceiling((top - itemHeight) / stepY));
        var lastY = (int)Math.Floor(bottom / stepY);
        if (right < 0 || bottom < 0)
        {
            yield break;
        }

        // Thumbnails fill rows left to right (perLine columns); List fills columns top to bottom (perLine rows).
        if (columnMajor)
        {
            lastY = Math.Min(lastY, perLine - 1);
        }
        else
        {
            lastX = Math.Min(lastX, perLine - 1);
        }

        for (var y = firstY; y <= lastY; y++)
        {
            for (var x = firstX; x <= lastX; x++)
            {
                var index = columnMajor ? (x * perLine) + y : (y * perLine) + x;
                if (index >= count)
                {
                    if (columnMajor)
                    {
                        break;
                    }

                    yield break;
                }

                yield return index;
            }
        }
    }

    /// <summary>Distance for PageUp / PageDown: a screenful of lines, less one so the last line stays in view.</summary>
    public static int PageDelta(int visibleLines, int perLine) => Math.Max(1, visibleLines - 1) * perLine;
}
