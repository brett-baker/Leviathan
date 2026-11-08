namespace Leviathan.ViewModels.Preview;

/// <summary>A rectangle in a treemap, in the units of the area it was laid out in.</summary>
public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    /// <summary>The area of the rectangle.</summary>
    public double Area => Width * Height;
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing and van Wijk): each weight gets a rectangle whose area is proportional to it, and the
/// rectangles are kept as close to square as possible so small ones stay visible. Used by the mini space map in the folder preview (spec
/// §9.1) and meant to be reused by the full space map (§14). Pure arithmetic, so it is tested without a screen.
/// </summary>
public static class TreemapLayout
{
    /// <summary>
    /// Lays <paramref name="weights"/> out in a <paramref name="width"/> × <paramref name="height"/> area. The result has one rectangle per
    /// weight, in the same order; a weight of zero or less gets an empty rectangle.
    /// </summary>
    public static TreemapRect[] Layout(IReadOnlyList<double> weights, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var result = new TreemapRect[weights.Count];
        var order = Enumerable.Range(0, weights.Count).Where(i => weights[i] > 0).OrderByDescending(i => weights[i]).ToArray();
        if (order.Length == 0 || width <= 0 || height <= 0)
        {
            return result;
        }

        var total = order.Sum(i => weights[i]);
        var scale = width * height / total;
        double x = 0, y = 0, w = width, h = height;
        var next = 0;
        while (next < order.Length)
        {
            var side = Math.Min(w, h);
            var end = next + 1;
            var sum = weights[order[next]] * scale;
            var worst = Worst(sum, weights[order[next]] * scale, weights[order[next]] * scale, side);
            while (end < order.Length)
            {
                var area = weights[order[end]] * scale;
                var candidate = Worst(sum + area, weights[order[next]] * scale, area, side);
                if (candidate > worst)
                {
                    break;
                }

                sum += area;
                worst = candidate;
                end++;
            }

            // The row is a strip along the shorter side: a column on the left of a wide area, a band on top of a tall one.
            if (w >= h)
            {
                var strip = sum / h;
                var top = y;
                for (var k = next; k < end; k++)
                {
                    var rowHeight = weights[order[k]] * scale / strip;
                    result[order[k]] = new TreemapRect(x, top, strip, rowHeight);
                    top += rowHeight;
                }

                x += strip;
                w -= strip;
            }
            else
            {
                var strip = sum / w;
                var left = x;
                for (var k = next; k < end; k++)
                {
                    var columnWidth = weights[order[k]] * scale / strip;
                    result[order[k]] = new TreemapRect(left, y, columnWidth, strip);
                    left += columnWidth;
                }

                y += strip;
                h -= strip;
            }

            next = end;
        }

        return result;
    }

    // The worst aspect ratio in a row whose areas add up to sum, with the largest and smallest given, along a side of the given length.
    private static double Worst(double sum, double largest, double smallest, double side)
    {
        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * largest / sum2, sum2 / (s2 * smallest));
    }
}
