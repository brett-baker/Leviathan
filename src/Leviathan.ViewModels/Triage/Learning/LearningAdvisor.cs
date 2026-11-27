using System.Globalization;

namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>What the learning thinks of one file: the answer, how sure it is, why, and how often it has been right lately.</summary>
/// <param name="Keep">True when it thinks the user would keep the file.</param>
/// <param name="Confidence">How sure, 0.5 to 1.</param>
/// <param name="Reasons">The main things in the file that lead to the answer, in plain words (up to three).</param>
/// <param name="RecentAccuracy">The share it got right on the user's latest decisions, 0 to 1.</param>
/// <param name="Tested">How many latest decisions that is measured on.</param>
/// <param name="ModelName">Which model made the guess.</param>
public sealed record TriageHint(bool Keep, double Confidence, IReadOnlyList<string> Reasons, double RecentAccuracy, int Tested, string ModelName)
{
    /// <summary>The short form shown on screen, such as "Keep 69%".</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{(Keep ? "Keep" : "Discard")} {Confidence:P0}");

    /// <summary>The longer form for a tooltip: the reasons, and how far to trust it.</summary>
    public string Detail =>
        (Reasons.Count == 0 ? "No single thing stands out." : "Because " + string.Join(", ", Reasons) + ".")
        + string.Create(CultureInfo.InvariantCulture, $" Right {RecentAccuracy:P0} of the time on your latest {Tested} decisions ({ModelName}). It is only a guess: the choice is yours.");
}

/// <summary>
/// Gives the hint for the file on screen (triage learning plan, slice 5). Trains the baseline and the logistic model on the earlier
/// decisions, judges both on the latest ones, and uses the logistic model only when it beats the baseline. Gives no hint until the
/// examples are enough (<see cref="LearningProgress.Ready"/>) and the chosen model is clearly better than guessing. The trained model is
/// kept until a new file's facts arrive. Everything runs off the UI thread and a failure means no hint, never an error on screen.
/// </summary>
public sealed class LearningAdvisor(ITriageHistory history, IFileFactStore store, FeatureCollector collector) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State? _state;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private sealed record State(int Complete, IDiscardModel? Model, string Name, AccuracyReport Report);

    /// <summary>The hint for a file, or null when there is not enough to say (or the chosen model is no better than guessing).</summary>
    public async Task<TriageHint?> HintAsync(string path, long size, long modifiedTicks, CancellationToken cancellationToken)
    {
        var state = await StateAsync(cancellationToken).ConfigureAwait(false);
        if (state.Model is null)
        {
            return null;
        }

        var stored = await store.GetAsync(path, cancellationToken).ConfigureAwait(false);
        var facts = await collector.GatherAsync(path, size, modifiedTicks, stored, cancellationToken).ConfigureAwait(false);
        var features = LearningFeatures.Of(facts);
        var discard = state.Model.DiscardProbability(features);
        var keep = discard <= 0.5;
        var reasons = state.Model.Contributions(features)
            .Where(c => keep ? c.Weight < -0.3 : c.Weight > 0.3)
            .OrderByDescending(c => Math.Abs(c.Weight))
            .Take(3)
            .Select(c => LearningFeatures.Describe(c.Feature))
            .ToList();
        return new TriageHint(keep, Math.Max(discard, 1 - discard), reasons, state.Report.Accuracy, state.Report.Tested, state.Name);
    }

    /// <summary>Which model is in use and how it did on the latest decisions, in words, for Settings.</summary>
    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var state = await StateAsync(cancellationToken).ConfigureAwait(false);
        return state.Report.Enough
            ? $"{state.Name}. {state.Report.Text}" + (state.Model is null ? " No hints are shown." : string.Empty)
            : state.Report.Text;
    }

    private async Task<State> StateAsync(CancellationToken cancellationToken)
    {
        var complete = (await store.CountAsync(cancellationToken).ConfigureAwait(false)).Complete;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is { } cached && cached.Complete == complete)
            {
                return cached;
            }

            return _state = await BuildAsync(complete, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<State> BuildAsync(int complete, CancellationToken cancellationToken)
    {
        var decided = TriageHistoryReplay.Examples(await history.LoadAsync(cancellationToken).ConfigureAwait(false));
        var all = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var byPath = new Dictionary<string, FileFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all.Where(f => f.Complete))
        {
            byPath[f.Path] = f;
        }

        var examples = decided.Where(e => byPath.ContainsKey(e.Path)).Select(e => (e, byPath[e.Path])).ToList();
        var progress = LearningProgress.From(decided, new HashSet<string>(byPath.Keys, StringComparer.OrdinalIgnoreCase));
        var baseline = AccuracyReport.Evaluate(examples, BaselineModel.Train);
        var logistic = AccuracyReport.Evaluate(examples, LogisticModel.Train);
        var useLogistic = logistic.Enough && logistic.Accuracy > baseline.Accuracy;
        var report = useLogistic ? logistic : baseline;
        var name = useLogistic ? "logistic model" : "word-count model";
        if (!progress.Ready || !report.Useful)
        {
            return new State(complete, null, name, report);
        }

        var trainer = useLogistic ? (ModelTrainer)LogisticModel.Train : BaselineModel.Train;
        var model = trainer([.. examples.Select(e => (LearningFeatures.Of(e.Item2), e.e.Decision == TriageDecision.Discarded))]);
        return new State(complete, model, name, report);
    }
}
