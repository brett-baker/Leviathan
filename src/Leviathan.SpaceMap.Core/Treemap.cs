namespace Leviathan.SpaceMap;

/// <summary>A rectangle in pixels.</summary>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct TileRect(double X, double Y, double Width, double Height)
{
    /// <summary>Right edge.</summary>
    public double Right => X + Width;

    /// <summary>Bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>Area in square pixels.</summary>
    public double Area => Width * Height;

    /// <summary>True when the point is inside (left and top edges included, right and bottom excluded).</summary>
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>One rectangle of the map.</summary>
/// <param name="Node">The file, the folder with no contents to show, or - for a remainder - the folder whose small items the tile stands for.</param>
/// <param name="Bounds">Where it is drawn.</param>
/// <param name="Depth">How many folders deep it is below the root of the map.</param>
/// <param name="IsRemainder">True when the tile stands for all the items of <see cref="Node"/> that are too small to have a tile of their own.</param>
public sealed record TreemapTile(SpaceNode Node, TileRect Bounds, int Depth, bool IsRemainder = false);

/// <summary>Switches for the layout.</summary>
public sealed record TreemapOptions
{
    /// <summary>A folder or file whose rectangle would have less area than this (square pixels) is not split further; default 6.</summary>
    public double MinTileArea { get; init; } = 6;

    /// <summary>Space left around the contents of a folder so nested folders can be told apart, in pixels; default 1.</summary>
    public double FolderPadding { get; init; } = 1;

    /// <summary>Hides files smaller than this many bytes ("show only files over a size", spec §14); default 0 (all).</summary>
    public long MinBytes { get; init; }
}

/// <summary>
/// The squarified treemap of Bruls, Huizing and van Wijk (spec §14): every file is a rectangle with an area in proportion to its bytes, laid out so the
/// rectangles are as close to square as possible, which keeps them readable. Folders are laid out inside their own rectangle, so the map shows where
/// the space is. Anything too small to see is drawn as one block for the folder that holds it, which bounds the work by the number of pixels, not by the
/// million files behind them.
/// </summary>
public static class Treemap
{
    /// <summary>Lays out the contents of <paramref name="root"/> inside <paramref name="bounds"/>. The result holds one tile per file shown (and per small folder drawn as a block).</summary>
    public static IReadOnlyList<TreemapTile> Layout(SpaceNode root, TileRect bounds, TreemapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var settings = options ?? new TreemapOptions();
        var tiles = new List<TreemapTile>();
        if (bounds.Width <= 0 || bounds.Height <= 0 || root.Size <= 0)
        {
            return tiles;
        }

        if (!root.IsFolder)
        {
            tiles.Add(new TreemapTile(root, bounds, 0));
            return tiles;
        }

        LayoutFolder(root, bounds, 0, settings, tiles);
        return tiles;
    }

    /// <summary>The tile under a point, or null. The last tile that holds the point wins, so a block drawn over its folder's frame is the one found.</summary>
    public static TreemapTile? HitTest(IReadOnlyList<TreemapTile> tiles, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        for (var i = tiles.Count - 1; i >= 0; i--)
        {
            if (tiles[i].Bounds.Contains(x, y))
            {
                return tiles[i];
            }
        }

        return null;
    }

    /// <summary>The tile that shows <paramref name="node"/>: its own, or the remainder tile of the nearest folder above it whose small items are drawn together. Null when it is not on the map.</summary>
    public static TreemapTile? TileOf(IReadOnlyList<TreemapTile> tiles, SpaceNode node)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(node);
        for (var candidate = node; candidate is not null; candidate = candidate.Parent)
        {
            foreach (var tile in tiles)
            {
                if (tile.Node == candidate)
                {
                    return tile;
                }
            }
        }

        return null;
    }

    private static void LayoutFolder(SpaceNode folder, TileRect bounds, int depth, TreemapOptions options, List<TreemapTile> tiles)
    {
        var inner = Inset(bounds, depth == 0 ? 0 : options.FolderPadding);
        var children = new List<SpaceNode>(folder.Children.Count);
        long total = 0;
        foreach (var child in folder.Children)
        {
            if (child.Size > 0 && child.Size >= options.MinBytes)
            {
                children.Add(child);
                total += child.Size;
            }
        }

        if (children.Count == 0 || total <= 0 || inner.Width <= 0 || inner.Height <= 0)
        {
            return;
        }

        children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        var scale = inner.Area / total;

        // Items too small to be seen are drawn together as one remainder tile for this folder, which keeps the number of tiles bounded by the pixels on screen
        // however many files there are, and still gives their bytes the area they own.
        var shown = children.Count;
        while (shown > 0 && children[shown - 1].Size * scale < options.MinTileArea)
        {
            shown--;
        }

        var hasRemainder = shown < children.Count;
        var count = shown + (hasRemainder ? 1 : 0);
        var areas = new double[count];
        for (var i = 0; i < shown; i++)
        {
            areas[i] = children[i].Size * scale;
        }

        if (hasRemainder)
        {
            long rest = 0;
            for (var i = shown; i < children.Count; i++)
            {
                rest += children[i].Size;
            }

            areas[shown] = rest * scale;
        }

        var rects = Squarify(areas, inner);
        for (var i = 0; i < count; i++)
        {
            var rect = rects[i];
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            if (i == shown && hasRemainder)
            {
                tiles.Add(new TreemapTile(folder, rect, depth + 1, IsRemainder: true));
                continue;
            }

            var child = children[i];
            if (!child.IsFolder || child.Children.Count == 0)
            {
                tiles.Add(new TreemapTile(child, rect, depth + 1));
            }
            else
            {
                LayoutFolder(child, rect, depth + 1, options, tiles);
            }
        }
    }

    private static TileRect Inset(TileRect rect, double padding) =>
        padding <= 0 || rect.Width <= 2 * padding || rect.Height <= 2 * padding
            ? rect
            : new TileRect(rect.X + padding, rect.Y + padding, rect.Width - (2 * padding), rect.Height - (2 * padding));

    // Bruls et al.: fill rows along the shorter side, adding items while the worst aspect ratio of the row does not get worse.
    private static TileRect[] Squarify(double[] areas, TileRect bounds)
    {
        var result = new TileRect[areas.Length];
        var remaining = bounds;
        var start = 0;
        while (start < areas.Length)
        {
            var side = Math.Min(remaining.Width, remaining.Height);
            var end = start + 1;
            var rowSum = areas[start];
            var worst = Worst(areas, start, end, rowSum, side);
            while (end < areas.Length)
            {
                var nextSum = rowSum + areas[end];
                var nextWorst = Worst(areas, start, end + 1, nextSum, side);
                if (nextWorst > worst)
                {
                    break;
                }

                rowSum = nextSum;
                worst = nextWorst;
                end++;
            }

            // Lay the row along the shorter side; the rest of the space is what is left after the row's thickness.
            var horizontal = remaining.Width >= remaining.Height; // a column on the left when the space is wider than tall
            var thickness = side > 0 ? rowSum / side : 0;
            if (end == areas.Length)
            {
                // The last row takes all that is left, so rounding never leaves a gap.
                thickness = horizontal ? remaining.Width : remaining.Height;
            }

            var offset = 0.0;
            for (var i = start; i < end; i++)
            {
                var length = rowSum > 0 ? areas[i] / rowSum * side : 0;
                result[i] = horizontal
                    ? new TileRect(remaining.X, remaining.Y + offset, thickness, length)
                    : new TileRect(remaining.X + offset, remaining.Y, length, thickness);
                offset += length;
            }

            remaining = horizontal
                ? new TileRect(remaining.X + thickness, remaining.Y, Math.Max(0, remaining.Width - thickness), remaining.Height)
                : new TileRect(remaining.X, remaining.Y + thickness, remaining.Width, Math.Max(0, remaining.Height - thickness));
            start = end;
        }

        return result;
    }

    private static double Worst(double[] areas, int start, int end, double sum, double side)
    {
        if (sum <= 0 || side <= 0)
        {
            return double.MaxValue;
        }

        var max = areas[start];
        var min = areas[end - 1];
        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }
}
