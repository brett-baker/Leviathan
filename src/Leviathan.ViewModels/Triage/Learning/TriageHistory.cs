namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>
/// One triage decision as it happened, kept for good: the examples the recommendations learn from. Unlike the journal, which is cleared
/// once a kept file has been moved, nothing here is ever compacted away.
/// </summary>
/// <param name="Time">When the decision was made.</param>
/// <param name="Path">The file as it was when triage began (where it came from, not where a keep moved it).</param>
/// <param name="Decision">Kept, Discarded, Skipped, or None for a decision taken back.</param>
/// <param name="ReasonId">The <see cref="RejectReason"/> given with a discard, or null.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="ModifiedTicks">The file's last-write time in UTC ticks.</param>
public sealed record TriageDecisionRecord(DateTimeOffset Time, string Path, TriageDecision Decision, string? ReasonId, long Size, long ModifiedTicks);

/// <summary>Where triage decisions are kept for the learning (append-only; every call is off the UI thread).</summary>
public interface ITriageHistory
{
    /// <summary>Adds one decision.</summary>
    Task AppendAsync(TriageDecisionRecord record, CancellationToken cancellationToken);

    /// <summary>Every decision made so far, oldest first.</summary>
    Task<IReadOnlyList<TriageDecisionRecord>> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Turns the log of decisions into the examples to learn from.</summary>
public static class TriageHistoryReplay
{
    /// <summary>
    /// The latest decision of each file that ended up kept or discarded, oldest file first. A decision taken back (None) and a skip
    /// remove the file from the examples, so only what the user finally decided teaches anything.
    /// </summary>
    public static IReadOnlyList<TriageDecisionRecord> Examples(IEnumerable<TriageDecisionRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var latest = new Dictionary<string, TriageDecisionRecord>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var e in events)
        {
            if (!latest.ContainsKey(e.Path))
            {
                order.Add(e.Path);
            }

            latest[e.Path] = e;
        }

        return [.. order.Select(p => latest[p]).Where(r => r.Decision is TriageDecision.Kept or TriageDecision.Discarded)];
    }
}
