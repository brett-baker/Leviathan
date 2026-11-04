using Leviathan.Contracts;

namespace Leviathan.ViewModels.Hub;

/// <summary>How a strip line is drawn (spec §12.6).</summary>
public enum StripLineStyle
{
    /// <summary>A solid line filling left to right.</summary>
    Solid,

    /// <summary>A moving shimmer: the total is not known yet (enumerating).</summary>
    Shimmer,

    /// <summary>A dashed line: the job is paused.</summary>
    Dashed,

    /// <summary>A pulsing line: the job waits for a share to answer.</summary>
    Pulse,
}

/// <summary>One line of the activity strip.</summary>
/// <param name="JobId">The job the line stands for; null for the merged line.</param>
/// <param name="ColorIndex">The job's colour (0 to 7), or -1 for the neutral grey of a merged line.</param>
/// <param name="Fraction">How much of the line is filled, 0 to 1.</param>
/// <param name="Style">Solid, shimmer, dashed or pulse.</param>
/// <param name="RedTip">True when the job has failed items nobody has looked at yet.</param>
/// <param name="Tooltip">Job name, percent, bytes, speed and ETA (the merged line lists its jobs).</param>
/// <param name="MergedCount">For the merged line, how many jobs it stands for; otherwise 0.</param>
public sealed record StripLine(Guid? JobId, int ColorIndex, double Fraction, StripLineStyle Style, bool RedTip, string Tooltip, int MergedCount);

/// <summary>
/// What the activity strip under the address bar draws (spec §12.6): one thin line per running job, at most
/// <see cref="MaxLines"/>; when there are more jobs the last slot becomes one combined line with a "+N" badge. Paused, waiting and
/// still-enumerating jobs look different from running ones, and a job with failed items keeps a red tip until its report is opened.
/// Queued jobs have no line yet. When nothing is going on the list is empty and the strip takes no space. The model only reads
/// <see cref="OperationsModel"/>, so it needs no UI thread.
/// </summary>
public sealed class ActivityStripModel
{
    /// <summary>How many lines stack before the rest merge into the last one.</summary>
    public const int MaxLines = 4;

    private readonly OperationsModel _operations;

    /// <summary>Creates the model over the operations list.</summary>
    public ActivityStripModel(OperationsModel operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations;
    }

    /// <summary>The lines as they are now, top to bottom.</summary>
    public IReadOnlyList<StripLine> Lines
    {
        get
        {
            var shown = _operations.Entries.Where(e => e.State != JobState.Queued && (e.IsActive || e.NeedsAttention)).ToList();
            if (shown.Count <= MaxLines)
            {
                return [.. shown.Select(Single)];
            }

            var lines = shown.Take(MaxLines - 1).Select(Single).ToList();
            lines.Add(Merge([.. shown.Skip(MaxLines - 1)]));
            return lines;
        }
    }

    private static StripLine Single(OperationEntry entry) => new(
        entry.Id,
        entry.ColorIndex,
        Math.Clamp((entry.Percent ?? 0) / 100.0, 0, 1),
        StyleOf(entry),
        entry.NeedsAttention,
        entry.Tooltip,
        0);

    private static StripLineStyle StyleOf(OperationEntry entry) => entry.State switch
    {
        JobState.Paused => StripLineStyle.Dashed,
        JobState.Waiting => StripLineStyle.Pulse,
        _ when entry.IsActive && entry.Percent is null => StripLineStyle.Shimmer,
        _ => StripLineStyle.Solid,
    };

    private static StripLine Merge(IReadOnlyList<OperationEntry> entries)
    {
        var known = entries.Where(e => e.Percent is not null).Select(e => e.Percent!.Value).ToList();
        var fraction = known.Count == 0 ? 0 : known.Average() / 100.0;
        var tooltip = string.Join(Environment.NewLine, entries.Select(e => e.Title));
        return new StripLine(null, -1, Math.Clamp(fraction, 0, 1), known.Count == 0 ? StripLineStyle.Shimmer : StripLineStyle.Solid, entries.Any(e => e.NeedsAttention), tooltip, entries.Count);
    }
}
