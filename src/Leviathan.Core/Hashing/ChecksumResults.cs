namespace Leviathan.Core.Hashing;

/// <summary>
/// How a checksum job hands its sums over (spec §15.4): the sums of one file travel as the text of an item's result, and a file that
/// failed verification says why in words that start with a fixed marker. The worker writes them and the window reads them, so both live
/// here. Pure.
/// </summary>
public static class ChecksumResults
{
    /// <summary>The start of a failed item's reason when the file's sum is not the one the checksum file says.</summary>
    public const string Mismatch = "MISMATCH";

    /// <summary>The start of a failed item's reason when the file a checksum file names is not there.</summary>
    public const string Missing = "MISSING";

    /// <summary>Writes the sums of one file the way an item's result carries them: <c>Sha256=hex;Md5=hex</c>.</summary>
    public static string Encode(IReadOnlyDictionary<ChecksumKind, string> sums)
    {
        ArgumentNullException.ThrowIfNull(sums);
        return string.Join(';', sums.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>Reads what <see cref="Encode"/> wrote; a piece that cannot be read is left out.</summary>
    public static IReadOnlyDictionary<ChecksumKind, string> Decode(string? result)
    {
        var sums = new Dictionary<ChecksumKind, string>();
        foreach (var piece in (result ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = piece.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && Enum.TryParse<ChecksumKind>(piece[..at], out var kind))
            {
                sums[kind] = piece[(at + 1)..];
            }
        }

        return sums;
    }
}
