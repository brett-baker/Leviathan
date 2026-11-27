namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>
/// Logistic regression over <see cref="LearningFeatures"/> (triage learning plan, slice 5): one weight per feature, learned by a few
/// passes of stochastic gradient descent with a small penalty on big weights. Unlike naive Bayes it does not count a feature twice when
/// two features always come together, so it can pick out what really separates a discard from a keep. Deterministic (a fixed shuffle) so
/// the same decisions always give the same hints. A feature seen in no example is ignored.
/// </summary>
public sealed class LogisticModel : IDiscardModel
{
    private const int Passes = 40;
    private const double Rate = 0.2;
    private const double Penalty = 0.002;

    private readonly Dictionary<string, double> _weights;
    private readonly double _bias;

    private LogisticModel(Dictionary<string, double> weights, double bias)
    {
        _weights = weights;
        _bias = bias;
    }

    /// <summary>Learns from the examples (features of a file, whether it was discarded).</summary>
    public static IDiscardModel Train(IReadOnlyList<(IReadOnlyCollection<string> Features, bool Discarded)> examples)
    {
        ArgumentNullException.ThrowIfNull(examples);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var discards = examples.Count(e => e.Discarded);
        var keeps = examples.Count - discards;
        var bias = discards == 0 || keeps == 0 ? 0 : Math.Log((double)discards / keeps);
        var order = Enumerable.Range(0, examples.Count).ToArray();
        var random = new Random(12345);
        foreach (var (features, _) in examples)
        {
            foreach (var feature in features)
            {
                weights.TryAdd(feature, 0);
            }
        }

        for (var pass = 0; pass < Passes; pass++)
        {
            random.Shuffle(order);
            foreach (var index in order)
            {
                var (features, discarded) = examples[index];
                var sum = bias;
                foreach (var feature in features)
                {
                    sum += weights[feature];
                }

                var error = (discarded ? 1.0 : 0.0) - Sigmoid(sum);
                bias += Rate * error;
                foreach (var feature in features)
                {
                    weights[feature] += Rate * (error - Penalty * weights[feature]);
                }
            }
        }

        return new LogisticModel(weights, bias);
    }

    /// <inheritdoc />
    public double DiscardProbability(IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var sum = _bias;
        foreach (var feature in features)
        {
            sum += _weights.GetValueOrDefault(feature);
        }

        return Sigmoid(sum);
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Feature, double Weight)> Contributions(IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return [.. features.Where(_weights.ContainsKey).Select(f => (f, _weights[f]))];
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -30, 30)));
}
