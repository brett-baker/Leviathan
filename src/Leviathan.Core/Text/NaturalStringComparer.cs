namespace Leviathan.Core.Text;

/// <summary>
/// Compares names the way people expect: digit runs compare by value, so <c>file2</c> sorts before <c>file10</c>
/// (spec §8.3). Letters compare case-insensitively; ties fall back to ordinal comparison so the order is total and stable.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    /// <summary>Shared instance.</summary>
    public static NaturalStringComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var result = CompareNatural(x.AsSpan(), y.AsSpan());
        return result != 0 ? result : string.CompareOrdinal(x, y);
    }

    private static int CompareNatural(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var runX = x[startX..i].TrimStart('0');
                var runY = y[startY..j].TrimStart('0');

                // Longer run (after stripping leading zeros) is the bigger number; no overflow for any length.
                if (runX.Length != runY.Length)
                {
                    return runX.Length.CompareTo(runY.Length);
                }

                var digits = runX.SequenceCompareTo(runY);
                if (digits != 0)
                {
                    return Math.Sign(digits);
                }

                // Same value: fewer leading zeros first ("1" before "01").
                var zeros = (i - startX).CompareTo(j - startY);
                if (zeros != 0)
                {
                    return zeros;
                }

                continue;
            }

            var cx = char.ToUpperInvariant(x[i]);
            var cy = char.ToUpperInvariant(y[j]);
            if (cx != cy)
            {
                return cx.CompareTo(cy);
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
