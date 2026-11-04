namespace Leviathan.ViewModels.Hub;

/// <summary>
/// The points of a speed sparkline (spec §12.6): one sample per second, newest on the right, scaled so the fastest sample reaches the
/// top. Kept apart from the control that draws it so the arithmetic is tested.
/// </summary>
public static class Sparkline
{
    /// <summary>
    /// Points for a line of <paramref name="width"/> by <paramref name="height"/> pixels, y growing downwards. Fewer than two samples
    /// give none (a dot is not a trend). With <paramref name="ceiling"/> zero the fastest sample sets the scale.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Points(IReadOnlyList<double> values, double width, double height, double ceiling = 0)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2 || width <= 0 || height <= 0)
        {
            return [];
        }

        var top = ceiling > 0 ? ceiling : values.Max();
        var points = new (double X, double Y)[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var fraction = top <= 0 ? 0 : Math.Clamp(values[i] / top, 0, 1);
            points[i] = (width * i / (values.Count - 1), height - (height * fraction));
        }

        return points;
    }

    /// <summary>
    /// The speeds of several jobs added together, second by second. The newest sample of every job is the same moment, so the lists
    /// are lined up at their ends; a job that started later contributes from where its history begins.
    /// </summary>
    public static IReadOnlyList<double> Sum(IEnumerable<IReadOnlyList<double>> histories)
    {
        ArgumentNullException.ThrowIfNull(histories);
        var all = histories.Where(h => h.Count > 0).ToList();
        if (all.Count == 0)
        {
            return [];
        }

        var length = all.Max(h => h.Count);
        var total = new double[length];
        foreach (var history in all)
        {
            var offset = length - history.Count;
            for (var i = 0; i < history.Count; i++)
            {
                total[offset + i] += history[i];
            }
        }

        return total;
    }
}
