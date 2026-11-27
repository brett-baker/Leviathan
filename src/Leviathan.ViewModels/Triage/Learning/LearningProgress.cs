using System.Globalization;

namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>
/// How far the learning is from having enough examples to suggest anything (triage learning plan, slice 4). It needs both kinds: with
/// 200 keeps and 4 discards a model cannot tell them apart, so the bar fills only as far as the thinner side allows.
/// </summary>
/// <param name="Keeps">Decided files kept that have complete facts.</param>
/// <param name="Discards">Decided files discarded that have complete facts.</param>
/// <param name="Target">How many of each are wanted.</param>
public sealed record LearningProgress(int Keeps, int Discards, int Target = LearningProgress.DefaultTarget)
{
    /// <summary>How many keeps and how many discards make "enough" to start. The model keeps improving after it.</summary>
    public const int DefaultTarget = 100;

    /// <summary>0 to 1: each kind counts up to <see cref="Target"/>, half the bar each.</summary>
    public double Fraction => Target <= 0 ? 1 : (Math.Min(Keeps, Target) + (double)Math.Min(Discards, Target)) / (2.0 * Target);

    /// <summary>True when both kinds have reached the target.</summary>
    public bool Ready => Keeps >= Target && Discards >= Target;

    /// <summary>The short label beside the bar.</summary>
    public string Label => Ready
        ? "Learning · enough examples"
        : string.Create(CultureInfo.InvariantCulture, $"Learning · {Keeps} kept, {Discards} discarded");

    /// <summary>What is missing, for the tooltip.</summary>
    public string Detail
    {
        get
        {
            if (Ready)
            {
                return "It has enough examples and keeps improving with every decision.";
            }

            var needKeeps = Math.Max(0, Target - Keeps);
            var needDiscards = Math.Max(0, Target - Discards);
            var parts = new List<string>();
            if (needKeeps > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{needKeeps} more kept"));
            }

            if (needDiscards > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{needDiscards} more discarded"));
            }

            return "Learning from your decisions. Suggestions start after about " + Target.ToString(CultureInfo.InvariantCulture) + " of each: " + string.Join(" and ", parts) + " to go.";
        }
    }

    /// <summary>
    /// The progress of <paramref name="examples"/> (the latest decision of each file) counting only files whose facts are complete,
    /// because an example with no facts teaches nothing.
    /// </summary>
    public static LearningProgress From(IEnumerable<TriageDecisionRecord> examples, ISet<string> withFacts, int target = DefaultTarget)
    {
        ArgumentNullException.ThrowIfNull(examples);
        ArgumentNullException.ThrowIfNull(withFacts);
        var keeps = 0;
        var discards = 0;
        foreach (var example in examples)
        {
            if (!withFacts.Contains(example.Path))
            {
                continue;
            }

            if (example.Decision == TriageDecision.Kept)
            {
                keeps++;
            }
            else if (example.Decision == TriageDecision.Discarded)
            {
                discards++;
            }
        }

        return new LearningProgress(keeps, discards, target);
    }
}
