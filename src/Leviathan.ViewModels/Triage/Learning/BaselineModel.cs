using System.Globalization;

namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>A learned guess at whether the user would discard a file.</summary>
public interface IDiscardModel
{
    /// <summary>The chance, 0 to 1, that the file is one the user would discard.</summary>
    double DiscardProbability(IEnumerable<string> features);

    /// <summary>What each known feature of the file pushes towards: above 0 towards discard, below 0 towards keep. Used to say why.</summary>
    IReadOnlyList<(string Feature, double Weight)> Contributions(IEnumerable<string> features);
}

/// <summary>Learns a model from decided files: the features of each and whether it was discarded.</summary>
public delegate IDiscardModel ModelTrainer(IReadOnlyList<(IReadOnlyCollection<string> Features, bool Discarded)> examples);

/// <summary>The words a file is described to the learning with: what its name, video and servers say. One string per fact, such as <c>t:cadence</c> or <c>g:feet</c>.</summary>
public static class LearningFeatures
{
    /// <summary>The facts of <paramref name="facts"/> as features.</summary>
    public static IReadOnlyCollection<string> Of(FileFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var features = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in facts.Name.Tokens)
        {
            features.Add("t:" + token);
        }

        if (facts.Name.Folder.Length > 0)
        {
            features.Add("folder:" + facts.Name.Folder.ToLowerInvariant());
        }

        if (facts.Name.Year is { } year)
        {
            features.Add("year:" + year.ToString(CultureInfo.InvariantCulture));
        }

        if (facts.Size > 0)
        {
            features.Add("size:" + Math.Max(0, (int)Math.Log2(Math.Max(1, facts.Size / (1024 * 1024))) ).ToString(CultureInfo.InvariantCulture));
        }

        if (facts.Video is { } video)
        {
            if (video.DurationSeconds is { } seconds)
            {
                features.Add("len:" + LengthBucket(seconds / 60));
            }

            if (video.Height is { } height)
            {
                features.Add("h:" + (height / 120 * 120).ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(video.Codec))
            {
                features.Add("codec:" + video.Codec.ToLowerInvariant());
            }
        }

        foreach (var scene in facts.Scenes)
        {
            foreach (var performer in scene.Performers)
            {
                features.Add("p:" + performer.ToLowerInvariant());
            }

            foreach (var tag in scene.Tags)
            {
                features.Add("g:" + tag.ToLowerInvariant());
            }

            if (!string.IsNullOrEmpty(scene.Studio))
            {
                features.Add("studio:" + scene.Studio.ToLowerInvariant());
            }

            if (scene.Date is { Length: >= 4 } date)
            {
                features.Add("year:" + date[..4]);
            }
        }

        return features;
    }

    /// <summary>A feature said the way a person would, for "because …" (for example <c>t:bts</c> becomes <c>the name has "bts"</c>).</summary>
    public static string Describe(string feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var colon = feature.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return feature;
        }

        var value = feature[(colon + 1)..];
        return feature[..colon] switch
        {
            "t" => $"the name has \"{value}\"",
            "folder" => $"it is in {value}",
            "year" => $"from {value}",
            "size" => "its size",
            "len" => $"{value} minutes long",
            "h" => $"{value}p-class video",
            "codec" => $"{value} video",
            "p" => $"performer {value}",
            "g" => $"tag {value}",
            "studio" => $"studio {value}",
            _ => feature,
        };
    }

    private static string LengthBucket(double minutes) => minutes switch
    {
        < 5 => "<5",
        < 10 => "5-10",
        < 20 => "10-20",
        < 40 => "20-40",
        < 80 => "40-80",
        _ => "80+",
    };
}

/// <summary>
/// A naive Bayes classifier over <see cref="LearningFeatures"/>: simple, fast, needs no tuning and works with a few dozen examples, so
/// it is the baseline that tells whether the data holds a pattern at all. A feature seen in no example is ignored, never a surprise.
/// </summary>
public sealed class BaselineModel : IDiscardModel
{
    /// <summary>Learns from the examples (features of a file, whether it was discarded).</summary>
    public static IDiscardModel Train(IReadOnlyList<(IReadOnlyCollection<string> Features, bool Discarded)> examples)
    {
        var model = new BaselineModel();
        foreach (var (features, discarded) in examples)
        {
            model.Add(features, discarded);
        }

        return model;
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Feature, double Weight)> Contributions(IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var list = new List<(string, double)>();
        if (_discards == 0 || _keeps == 0)
        {
            return list;
        }

        foreach (var feature in features)
        {
            var inDiscards = _discard.GetValueOrDefault(feature);
            var inKeeps = _keep.GetValueOrDefault(feature);
            if (inDiscards + inKeeps > 0)
            {
                list.Add((feature, Math.Log((inDiscards + 1.0) / (_discards + 2.0)) - Math.Log((inKeeps + 1.0) / (_keeps + 2.0))));
            }
        }

        return list;
    }

    private readonly Dictionary<string, int> _discard = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _keep = new(StringComparer.Ordinal);
    private int _discards;
    private int _keeps;

    /// <summary>Learns from one decided file.</summary>
    public void Add(IEnumerable<string> features, bool discarded)
    {
        ArgumentNullException.ThrowIfNull(features);
        var counts = discarded ? _discard : _keep;
        if (discarded)
        {
            _discards++;
        }
        else
        {
            _keeps++;
        }

        foreach (var feature in features)
        {
            counts[feature] = counts.GetValueOrDefault(feature) + 1;
        }
    }

    /// <summary>The chance, 0 to 1, that the file is one the user would discard.</summary>
    public double DiscardProbability(IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (_discards == 0 || _keeps == 0)
        {
            return _discards == 0 && _keeps == 0 ? 0.5 : _discards == 0 ? 0 : 1;
        }

        var logOdds = Math.Log((double)_discards / _keeps);
        foreach (var feature in features)
        {
            var inDiscards = _discard.GetValueOrDefault(feature);
            var inKeeps = _keep.GetValueOrDefault(feature);
            if (inDiscards + inKeeps == 0)
            {
                continue;
            }

            logOdds += Math.Log((inDiscards + 1.0) / (_discards + 2.0)) - Math.Log((inKeeps + 1.0) / (_keeps + 2.0));
        }

        return 1.0 / (1.0 + Math.Exp(-Math.Clamp(logOdds, -30, 30)));
    }
}

/// <summary>What the baseline found when it was tried on decisions it had not seen.</summary>
/// <param name="Enough">False when there are too few decided files with facts (or too few of one kind) to say anything.</param>
/// <param name="Trained">Earlier decisions it learned from.</param>
/// <param name="Tested">The latest decisions it was asked about.</param>
/// <param name="Accuracy">The share it got right, 0 to 1.</param>
/// <param name="MajorityAccuracy">The share a guesser who always says the commoner answer would get right: the number to beat.</param>
/// <param name="DiscardPrecision">When it said "discard", the share that really were discards (null when it never said so).</param>
/// <param name="DiscardGuesses">How many times it said "discard".</param>
public sealed record AccuracyReport(bool Enough, int Trained, int Tested, double Accuracy, double MajorityAccuracy, double? DiscardPrecision, int DiscardGuesses)
{
    /// <summary>The fewest decided files with facts before a report means anything.</summary>
    public const int MinimumExamples = 30;

    /// <summary>The fewest of each kind.</summary>
    public const int MinimumPerKind = 5;

    /// <summary>True when it was right clearly more often than always guessing the commoner answer: the bar for showing hints at all.</summary>
    public bool Useful => Enough && Accuracy > MajorityAccuracy + 0.05;

    /// <summary>A plain sentence or two for Settings.</summary>
    public string Text
    {
        get
        {
            if (!Enough)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Not enough yet to say. It needs about {MinimumExamples} decided files with facts, at least {MinimumPerKind} kept and {MinimumPerKind} discarded.");
            }

            var text = string.Create(CultureInfo.InvariantCulture, $"Learned from {Trained} earlier decisions, then asked about your latest {Tested}: right {Accuracy:P0} of the time. Always guessing the commoner answer would be right {MajorityAccuracy:P0}.");
            text += DiscardPrecision is { } precision
                ? string.Create(CultureInfo.InvariantCulture, $" When it said discard ({DiscardGuesses} times) it was right {precision:P0} of the time.")
                : " It never said discard.";
            text += Useful ? " That is a real pattern." : " That is not clearly better than guessing, so more examples or more facts are needed.";
            return text;
        }
    }

    /// <summary>
    /// Tries the baseline the honest way: learn from the earlier 80% of the decisions (by time) and ask about the latest 20%, so it
    /// is judged on files it has not seen and on the direction the taste is moving. <paramref name="examples"/> are decided files with their facts.
    /// </summary>
    public static AccuracyReport Evaluate(IReadOnlyList<(TriageDecisionRecord Decision, FileFacts Facts)> examples, ModelTrainer? train = null)
    {
        ArgumentNullException.ThrowIfNull(examples);
        var ordered = examples.Where(e => e.Decision.Decision is TriageDecision.Kept or TriageDecision.Discarded).OrderBy(e => e.Decision.Time).ToList();
        var discards = ordered.Count(e => e.Decision.Decision == TriageDecision.Discarded);
        if (ordered.Count < MinimumExamples || discards < MinimumPerKind || ordered.Count - discards < MinimumPerKind)
        {
            return new AccuracyReport(false, 0, 0, 0, 0, null, 0);
        }

        var split = Math.Min(ordered.Count - 10, (int)(ordered.Count * 0.8));
        var model = (train ?? BaselineModel.Train)([.. ordered.Take(split).Select(e => (LearningFeatures.Of(e.Facts), e.Decision.Decision == TriageDecision.Discarded))]);

        var right = 0;
        var guessedDiscard = 0;
        var rightDiscard = 0;
        var testDiscards = 0;
        var test = ordered.Skip(split).ToList();
        foreach (var (decision, facts) in test)
        {
            var actual = decision.Decision == TriageDecision.Discarded;
            var guess = model.DiscardProbability(LearningFeatures.Of(facts)) > 0.5;
            testDiscards += actual ? 1 : 0;
            right += guess == actual ? 1 : 0;
            if (guess)
            {
                guessedDiscard++;
                rightDiscard += actual ? 1 : 0;
            }
        }

        var majority = (double)Math.Max(testDiscards, test.Count - testDiscards) / test.Count;
        return new AccuracyReport(true, split, test.Count, (double)right / test.Count, majority, guessedDiscard == 0 ? null : (double)rightDiscard / guessedDiscard, guessedDiscard);
    }
}
