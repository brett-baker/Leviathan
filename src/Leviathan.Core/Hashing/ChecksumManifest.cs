using System.Text;
using System.Text.RegularExpressions;

namespace Leviathan.Core.Hashing;

/// <summary>One line of a checksum file: which file, what its sum should be, and which kind of sum that is.</summary>
/// <param name="Name">The file's name or relative path as the checksum file spells it (slashes as written).</param>
/// <param name="Hash">The expected sum, lowercase hex.</param>
/// <param name="Kind">The kind, told from the length of the sum.</param>
public sealed record ChecksumEntry(string Name, string Hash, ChecksumKind Kind);

/// <summary>What reading a checksum file found.</summary>
/// <param name="Entries">The files and their sums.</param>
/// <param name="Skipped">Lines that were not a checksum line, for a note ("a line of 20 digits is no kind of sum").</param>
public sealed record ChecksumManifestContents(IReadOnlyList<ChecksumEntry> Entries, IReadOnlyList<string> Skipped);

/// <summary>
/// Reads and writes checksum files (spec §15.4: "verify against a .sha256 / .md5 file; export a checksum file"). The reader accepts what
/// the common tools write: GNU <c>sha256sum</c> lines (<c>hash  name</c> and <c>hash *name</c>), BSD lines (<c>SHA256 (name) = hash</c>),
/// SFV (<c>name crc</c>) and a file holding only a sum, which belongs to the file with the checksum file's name without its extension. The
/// kind of each sum is told from its length, so one file may mix kinds. The writer produces GNU lines (SFV for CRC32). Pure: no I/O.
/// </summary>
public static partial class ChecksumManifest
{
    /// <summary>Reads the text of a checksum file called <paramref name="manifestFileName"/> (its name decides what a lone sum belongs to).</summary>
    public static ChecksumManifestContents Parse(string text, string manifestFileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(manifestFileName);
        var entries = new List<ChecksumEntry>();
        var skipped = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (TryLine(line, manifestFileName, out var entry))
            {
                entries.Add(entry);
            }
            else
            {
                skipped.Add(line.Length > 80 ? line[..80] + "…" : line);
            }
        }

        return new ChecksumManifestContents(entries, skipped);
    }

    /// <summary>Writes a checksum file for <paramref name="entries"/> (name, sum) of one <paramref name="kind"/>.</summary>
    public static string Format(IEnumerable<(string Name, string Hash)> entries, ChecksumKind kind)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = new StringBuilder();
        foreach (var (name, hash) in entries)
        {
            var clean = name.Replace('\\', '/');
            if (kind == ChecksumKind.Crc32)
            {
                text.Append(clean).Append(' ').Append(hash.ToLowerInvariant()).Append("\r\n");
            }
            else
            {
                text.Append(hash.ToLowerInvariant()).Append(" *").Append(clean).Append('\n');
            }
        }

        return text.ToString();
    }

    private static bool TryLine(string line, string manifestFileName, out ChecksumEntry entry)
    {
        entry = null!;
        if (BsdLine().Match(line) is { Success: true } bsd)
        {
            return Make(bsd.Groups[2].Value, bsd.Groups[3].Value, out entry);
        }

        if (GnuLine().Match(line) is { Success: true } gnu)
        {
            return Make(gnu.Groups[3].Value, gnu.Groups[1].Value, out entry);
        }

        if (SfvLine().Match(line) is { Success: true } sfv)
        {
            return Make(sfv.Groups[1].Value, sfv.Groups[2].Value, out entry);
        }

        if (HexOnly().IsMatch(line))
        {
            var stem = Path.GetFileNameWithoutExtension(manifestFileName);
            return stem.Length > 0 && Make(stem, line, out entry);
        }

        return false;
    }

    private static bool Make(string name, string hash, out ChecksumEntry entry)
    {
        entry = null!;
        if (ChecksumKinds.FromHexLength(hash.Length) is not { } kind)
        {
            return false;
        }

        var clean = name.Trim();
        while (clean.StartsWith("./", StringComparison.Ordinal) || clean.StartsWith(".\\", StringComparison.Ordinal))
        {
            clean = clean[2..];
        }

        if (clean.Length == 0)
        {
            return false;
        }

        entry = new ChecksumEntry(clean, hash.ToLowerInvariant(), kind);
        return true;
    }

    [GeneratedRegex(@"^(MD5|SHA-?1|SHA-?256|SHA-?512|XXH3|CRC32) ?\((.+)\) ?= ?([0-9a-fA-F]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BsdLine();

    [GeneratedRegex(@"^\\?([0-9a-fA-F]{8,128}) ([ *])(.+)$")]
    private static partial Regex GnuLine();

    [GeneratedRegex(@"^(.+?)\s+([0-9a-fA-F]{8})$")]
    private static partial Regex SfvLine();

    [GeneratedRegex(@"^[0-9a-fA-F]{8,128}$")]
    private static partial Regex HexOnly();
}
