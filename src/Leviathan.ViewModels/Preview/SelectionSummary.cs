using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Preview;

/// <summary>One line of the type breakdown: what kind of file, how many, how many bytes.</summary>
public sealed record TypeShare(string Label, int Count, long Bytes);

/// <summary>
/// What the preview pane says about several selected items (spec §9.1: "count, total size, type breakdown"): how many files and
/// folders, the bytes in the files (folder totals are counted lazily elsewhere and are not guessed here), and the biggest kinds of file by
/// extension, with the rest folded into "other" so the list stays short.
/// </summary>
public sealed record SelectionSummary(int Files, int Folders, long FileBytes, IReadOnlyList<TypeShare> Types)
{
    /// <summary>How many kinds are listed before the rest are folded together.</summary>
    public const int MaxTypes = 5;

    /// <summary>"12 items · 10 files, 2 folders · 1.2 GB".</summary>
    public string Headline
    {
        get
        {
            var items = Files + Folders;
            var parts = new List<string> { items == 1 ? "1 item" : $"{items:N0} items" };
            parts.Add(Folders == 0 ? $"{Files:N0} files" : Files == 0 ? $"{Folders:N0} folders" : $"{Files:N0} files, {Folders:N0} folders");
            parts.Add(DisplayFormat.Size(FileBytes));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Summarises the selection.</summary>
    public static SelectionSummary From(IReadOnlyList<FileEntry> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var files = selection.Where(e => e.Kind == FileEntryKind.File).ToList();
        var groups = files
            .GroupBy(e => e.Extension.Length == 0 ? "no extension" : e.Extension.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(g => new TypeShare(g.Key, g.Count(), g.Sum(e => e.Size)))
            .OrderByDescending(t => t.Bytes)
            .ThenByDescending(t => t.Count)
            .ThenBy(t => t.Label, StringComparer.Ordinal)
            .ToList();
        if (groups.Count > MaxTypes)
        {
            var rest = groups.Skip(MaxTypes - 1).ToList();
            groups = [.. groups.Take(MaxTypes - 1), new TypeShare("other", rest.Sum(t => t.Count), rest.Sum(t => t.Bytes))];
        }

        return new SelectionSummary(files.Count, selection.Count - files.Count, files.Sum(e => e.Size), groups);
    }
}
