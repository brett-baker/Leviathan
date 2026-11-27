namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>
/// Answers the two questions the screens ask of the learning: how far along is it (the bar in the triage window), and does the data hold
/// a pattern (the accuracy report in Settings). Reads the decision history and the gathered facts; every call runs off the UI thread.
/// </summary>
public sealed class LearningService(ITriageHistory history, IFileFactStore facts)
{
    /// <summary>The counts of kept and discarded files that have complete facts.</summary>
    public async Task<LearningProgress> ProgressAsync(CancellationToken cancellationToken)
    {
        var examples = TriageHistoryReplay.Examples(await history.LoadAsync(cancellationToken).ConfigureAwait(false));
        var have = await facts.CompletePathsAsync(cancellationToken).ConfigureAwait(false);
        return LearningProgress.From(examples, have);
    }

    /// <summary>Tries a baseline model on the latest decisions (see <see cref="AccuracyReport.Evaluate"/>).</summary>
    public async Task<AccuracyReport> ReportAsync(CancellationToken cancellationToken)
    {
        var examples = TriageHistoryReplay.Examples(await history.LoadAsync(cancellationToken).ConfigureAwait(false));
        var all = await facts.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var byPath = new Dictionary<string, FileFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all.Where(f => f.Complete))
        {
            byPath[f.Path] = f;
        }

        return AccuracyReport.Evaluate([.. examples.Where(e => byPath.ContainsKey(e.Path)).Select(e => (e, byPath[e.Path]))]);
    }
}
