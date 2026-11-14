using Leviathan.Contracts;
using Leviathan.Core.Hashing;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Checksums;

/// <summary>How one file came out of a checksum job.</summary>
public enum ChecksumState
{
    /// <summary>Computed, or (when verifying) the sum is the one the checksum file gave.</summary>
    Ok,

    /// <summary>Verifying: the sum is not the one the checksum file gave.</summary>
    Mismatch,

    /// <summary>Verifying: the checksum file names a file that is not there.</summary>
    Missing,

    /// <summary>The file could not be read.</summary>
    Failed,

    /// <summary>Not looked at: it is not a file on this PC or share.</summary>
    Skipped,
}

/// <summary>One file in the checksums window.</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Sums">Its sums, by kind; empty when it could not be read.</param>
/// <param name="State">How it came out.</param>
/// <param name="Message">Why, for anything but <see cref="ChecksumState.Ok"/>; null otherwise.</param>
public sealed record ChecksumRow(string Path, IReadOnlyDictionary<ChecksumKind, string> Sums, ChecksumState State, string? Message);

/// <summary>What a checksum run found.</summary>
/// <param name="Rows">The files, in the order the worker read them.</param>
/// <param name="Notes">Remarks from the worker, such as lines of a checksum file that could not be read.</param>
public sealed record ChecksumRun(IReadOnlyList<ChecksumRow> Rows, IReadOnlyList<string> Notes);

/// <summary>Runs the worker's checksum job for the window (spec §15.4): the seam between the checksums window and the worker.</summary>
public interface IChecksumScanner
{
    /// <summary>
    /// Computes <paramref name="kinds"/> for <paramref name="sources"/> (files and folders), or, with <paramref name="verify"/>, checks every
    /// file the checksum files in <paramref name="sources"/> name. <paramref name="progress"/> gets one line of text. Cancelling the token
    /// cancels the job.
    /// </summary>
    Task<ChecksumRun> RunAsync(IReadOnlyList<string> sources, IReadOnlyList<ChecksumKind> kinds, bool verify, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// What the checksums window shows and writes (spec §15.4): rows from a job's items, the text of a checksum file for the sums of one
/// kind, the summary line, and the match for a sum the person pasted to compare. Pure, so the wording and the output are tested.
/// </summary>
public static class ChecksumView
{
    /// <summary>The rows for a finished job's items.</summary>
    public static IReadOnlyList<ChecksumRow> RowsFrom(IEnumerable<ItemOutcome> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items.Select(Row)];
    }

    /// <summary>The one-line summary: "12 files", "12 files · 2 do not match · 1 missing".</summary>
    public static string Summary(IReadOnlyList<ChecksumRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var mismatch = rows.Count(r => r.State == ChecksumState.Mismatch);
        var missing = rows.Count(r => r.State == ChecksumState.Missing);
        var failed = rows.Count(r => r.State is ChecksumState.Failed or ChecksumState.Skipped);
        var parts = new List<string> { rows.Count == 1 ? "1 file" : $"{rows.Count:N0} files" };
        if (mismatch > 0)
        {
            parts.Add(mismatch == 1 ? "1 does not match" : $"{mismatch:N0} do not match");
        }

        if (missing > 0)
        {
            parts.Add($"{missing:N0} missing");
        }

        if (failed > 0)
        {
            parts.Add($"{failed:N0} could not be read");
        }

        if (mismatch + missing + failed == 0 && rows.Count > 0 && rows.Any(r => r.State == ChecksumState.Ok))
        {
            parts.Add("all good");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The text of a checksum file for the sums of <paramref name="kind"/>: one line per file that has one, named relative to
    /// <paramref name="root"/> (the folder the checksum file will sit in) when the file is below it, else by its own name.
    /// </summary>
    public static string ChecksumFile(IReadOnlyList<ChecksumRow> rows, ChecksumKind kind, string root)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return ChecksumManifest.Format(rows.Where(r => r.Sums.ContainsKey(kind)).Select(r => (RelativeName(r.Path, root), r.Sums[kind])), kind);
    }

    /// <summary>Which rows have a sum equal to <paramref name="pasted"/> (any kind; spaces and a <c>0x</c> are ignored, case is not).</summary>
    public static IReadOnlyList<ChecksumRow> Matching(IReadOnlyList<ChecksumRow> rows, string pasted)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var wanted = Clean(pasted);
        return wanted.Length == 0 ? [] : [.. rows.Where(r => r.Sums.Values.Any(v => string.Equals(v, wanted, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>The kind a pasted sum can be, from its length; null when no kind has that length.</summary>
    public static ChecksumKind? KindOfPasted(string pasted) => ChecksumKinds.FromHexLength(Clean(pasted).Length);

    /// <summary>
    /// The folder a checksum file is saved in by default, and the one its names are relative to: the folder that was checked when one
    /// folder was, else the folder the checked files have in common.
    /// </summary>
    public static string DefaultFolder(IReadOnlyList<LocationPath> items, IReadOnlyList<ChecksumRow> rows)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(rows);
        if (items.Count == 1 && rows.Any(r => r.Path.StartsWith(items[0].Value.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            return items[0].Value;
        }

        string? common = null;
        foreach (var row in rows)
        {
            var folder = row.Path[..Math.Max(0, row.Path.LastIndexOf('\\'))];
            common = common is null ? folder : CommonPrefix(common, folder);
        }

        return common ?? string.Empty;
    }

    /// <summary>The name offered for a checksum file: the name of the one file or folder checked, with the extension of the kind.</summary>
    public static string DefaultFileName(IReadOnlyList<LocationPath> items, ChecksumKind kind)
    {
        ArgumentNullException.ThrowIfNull(items);
        var name = items.Count == 1 ? items[0].Name : items.Count > 1 ? (items[0].Parent?.Name ?? "checksums") : "checksums";
        return name.TrimEnd(':', '\\') + ChecksumKinds.Extension(kind);
    }

    /// <summary>The sum text of a path's name relative to a folder, with forward slashes.</summary>
    public static string RelativeName(string path, string root)
    {
        if (root.Length > 0)
        {
            var prefix = root.TrimEnd('\\') + "\\";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return path[prefix.Length..].Replace('\\', '/');
            }
        }

        return path[(path.LastIndexOf('\\') + 1)..];
    }

    private static string Clean(string text)
    {
        var t = new string([.. text.Where(c => !char.IsWhiteSpace(c))]);
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t;
    }

    // The longest run of whole folders two paths share.
    private static string CommonPrefix(string a, string b)
    {
        var left = a.Split('\\');
        var right = b.Split('\\');
        var n = 0;
        while (n < left.Length && n < right.Length && string.Equals(left[n], right[n], StringComparison.OrdinalIgnoreCase))
        {
            n++;
        }

        return string.Join('\\', left.Take(n));
    }

    private static ChecksumRow Row(ItemOutcome item)
    {
        var sums = ChecksumResults.Decode(item.Result);
        if (item.Status == ItemStatus.Skipped)
        {
            return new ChecksumRow(item.Source, sums, ChecksumState.Skipped, item.Error);
        }

        if (item.Status == ItemStatus.Done)
        {
            return new ChecksumRow(item.Source, sums, ChecksumState.Ok, null);
        }

        var error = item.Error ?? "The file could not be read.";
        var state = error.StartsWith(ChecksumResults.Mismatch, StringComparison.Ordinal) ? ChecksumState.Mismatch
            : error.StartsWith(ChecksumResults.Missing, StringComparison.Ordinal) ? ChecksumState.Missing
            : ChecksumState.Failed;
        return new ChecksumRow(item.Source, sums, state, error);
    }
}
