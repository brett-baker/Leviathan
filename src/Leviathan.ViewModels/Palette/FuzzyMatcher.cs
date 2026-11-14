namespace Leviathan.ViewModels.Palette;

/// <summary>
/// The fuzzy match behind both palettes (spec §5.3, §15.6): the typed letters must appear in the text in order, not next to each other
/// ("ntab" finds "New tab"), and the better the letters line up the higher the score: a letter at the start of a word, letters in a run,
/// and a match near the start all count for more. Case is ignored and spaces in the query are ignored. Pure and allocation-light: it runs
/// on every key press over a few thousand candidates.
/// </summary>
public static class FuzzyMatcher
{
    private const int Match = 10;
    private const int Consecutive = 15;
    private const int WordStart = 12;
    private const int TextStart = 8;
    private const int Gap = 1;
    private const int Unmatched = int.MinValue / 2;

    /// <summary>
    /// The score of <paramref name="query"/> against <paramref name="text"/> (higher is better), or null when the letters do not all occur
    /// in order. An empty query matches everything with score 0. <paramref name="matched"/> gets the index in <paramref name="text"/> of each
    /// matched letter, for drawing the match in bold.
    /// </summary>
    public static int? Score(string query, string text, List<int>? matched = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(text);
        matched?.Clear();
        var q = new string([.. query.Where(c => !char.IsWhiteSpace(c))]);
        if (q.Length == 0)
        {
            return 0;
        }

        if (q.Length > text.Length)
        {
            return null;
        }

        var n = q.Length;
        var m = text.Length;

        // best[i, j]: the best score matching the first i+1 letters of the query with letter i sitting at text[j]; from[i, j] is where letter i-1 sat.
        var best = new int[n, m];
        var from = new int[n, m];
        for (var j = 0; j < m; j++)
        {
            best[0, j] = Same(q[0], text[j]) ? Bonus(text, j) - (Gap * j / 2) : Unmatched;
            from[0, j] = -1;
        }

        for (var i = 1; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                best[i, j] = Unmatched;
                if (!Same(q[i], text[j]))
                {
                    continue;
                }

                var here = Bonus(text, j);
                for (var k = i - 1; k < j; k++)
                {
                    if (best[i - 1, k] == Unmatched)
                    {
                        continue;
                    }

                    var score = best[i - 1, k] + here + (k == j - 1 ? Consecutive : -Gap * (j - k - 1));
                    if (score > best[i, j])
                    {
                        best[i, j] = score;
                        from[i, j] = k;
                    }
                }
            }
        }

        var end = -1;
        var top = Unmatched;
        for (var j = 0; j < m; j++)
        {
            if (best[n - 1, j] > top)
            {
                top = best[n - 1, j];
                end = j;
            }
        }

        if (end < 0)
        {
            return null;
        }

        if (matched is not null)
        {
            var at = end;
            for (var i = n - 1; i >= 0; i--)
            {
                matched.Add(at);
                at = from[i, at];
            }

            matched.Reverse();
        }

        return top;
    }

    private static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    // What a letter at text[j] is worth by where it sits: the start of the text, the start of a word, or the middle of one.
    private static int Bonus(string text, int j)
    {
        var score = Match;
        if (j == 0)
        {
            return score + TextStart + WordStart;
        }

        var before = text[j - 1];
        if (before is ' ' or '-' or '_' or '.' or '/' or '\\' or ':' || (char.IsLower(before) && char.IsUpper(text[j])))
        {
            score += WordStart;
        }

        return score;
    }
}
