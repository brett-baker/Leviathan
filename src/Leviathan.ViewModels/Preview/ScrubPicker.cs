namespace Leviathan.ViewModels.Preview;

/// <summary>
/// Which frame of a video's strip goes with a moment or a place (spec §9.1: filmstrip and the frame shown when the pointer rests on the
/// seek bar). The strip is a row of frames taken at known seconds, 16 to 24 of them, so the answers are index arithmetic: kept out of
/// the controls so they are tested without a screen.
/// </summary>
public static class ScrubPicker
{
    /// <summary>The index of the frame taken nearest to <paramref name="seconds"/>; -1 when the strip has no frames.</summary>
    public static int NearestFrame(IReadOnlyList<double> frameSeconds, double seconds)
    {
        ArgumentNullException.ThrowIfNull(frameSeconds);
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < frameSeconds.Count; i++)
        {
            var distance = Math.Abs(frameSeconds[i] - seconds);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The moment at <paramref name="fraction"/> (0 to 1, clamped) of a file that is <paramref name="duration"/> long.</summary>
    public static TimeSpan TimeAt(double fraction, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? TimeSpan.Zero : duration * Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);

    /// <summary>The fraction of a bar <paramref name="width"/> wide that <paramref name="x"/> is at, inside the track that starts <paramref name="margin"/> in from each end.</summary>
    public static double FractionAt(double x, double width, double margin = 0)
    {
        var track = width - (2 * margin);
        return track <= 0 ? 0 : Math.Clamp((x - margin) / track, 0, 1);
    }

    /// <summary>
    /// The indices of <paramref name="fit"/> frames spread evenly across a strip of <paramref name="frames"/>, first and last included
    /// when there is room, so a narrow filmstrip still spans the whole video. At most <paramref name="frames"/> are returned.
    /// </summary>
    public static IReadOnlyList<int> Spread(int frames, int fit)
    {
        if (frames <= 0 || fit <= 0)
        {
            return [];
        }

        var count = Math.Min(frames, fit);
        if (count == 1)
        {
            return [frames / 2];
        }

        return [.. Enumerable.Range(0, count).Select(i => (int)Math.Round(i * (frames - 1) / (double)(count - 1)))];
    }
}
