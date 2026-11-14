using System.Globalization;

namespace Leviathan.Core.Compare;

/// <summary>
/// How a compare job hands one result to the window (spec §15.5): the text of an item's result. The worker writes it and the window
/// reads it, so both live here. Pure.
/// </summary>
public static class CompareCodec
{
    private const char Sep = '|';
    private const string Absent = "-";

    /// <summary>Writes <paramref name="entry"/> (without its path, which travels as the item's source).</summary>
    public static string Encode(CompareEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Join(
            Sep,
            ((int)entry.Status).ToString(CultureInfo.InvariantCulture),
            entry.IsDirectory ? "d" : "f",
            Side(entry.Left),
            Side(entry.Right),
            entry.LeftChecksum ?? Absent,
            entry.RightChecksum ?? Absent);
    }

    /// <summary>Reads what <see cref="Encode"/> wrote for <paramref name="path"/>; null when the text is not one.</summary>
    public static CompareEntry? Decode(string path, string? text)
    {
        var parts = (text ?? string.Empty).Split(Sep);
        if (parts.Length != 6 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var status) || !Enum.IsDefined((CompareStatus)status))
        {
            return null;
        }

        return new CompareEntry(path, parts[1] == "d", ReadSide(parts[2]), ReadSide(parts[3]), (CompareStatus)status, parts[4] == Absent ? null : parts[4], parts[5] == Absent ? null : parts[5]);
    }

    private static string Side(CompareSide? side) => side is { } s ? $"{s.Size.ToString(CultureInfo.InvariantCulture)}:{s.Modified.UtcTicks.ToString(CultureInfo.InvariantCulture)}" : Absent;

    private static CompareSide? ReadSide(string text)
    {
        if (text == Absent)
        {
            return null;
        }

        var at = text.IndexOf(':', StringComparison.Ordinal);
        return at > 0 && long.TryParse(text.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && long.TryParse(text.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            ? new CompareSide(size, new DateTimeOffset(ticks, TimeSpan.Zero))
            : null;
    }
}
