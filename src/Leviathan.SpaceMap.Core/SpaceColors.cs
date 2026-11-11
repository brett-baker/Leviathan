using Leviathan.Search;

namespace Leviathan.SpaceMap;

/// <summary>What the colours of the map stand for (spec §14: "color by age instead of type").</summary>
public enum ColorMode
{
    /// <summary>By kind of file: video, audio, pictures, documents, archives, code, other.</summary>
    Type,

    /// <summary>By how long ago the file was changed, which finds stale data.</summary>
    Age,
}

/// <summary>A group of tiles drawn in one colour, with what it holds.</summary>
/// <param name="Category">Index into the colour palette of the mode.</param>
/// <param name="Label">What the colour means ("Video", "Over 2 years").</param>
/// <param name="Bytes">Total size of the files in it.</param>
/// <param name="Files">How many files.</param>
public sealed record LegendEntry(int Category, string Label, long Bytes, int Files);

/// <summary>
/// Which colour group a file belongs to (spec §14). Only the group is decided here, as an index; the view picks the actual colours for its theme, so the map
/// stays readable in light and dark.
/// </summary>
public static class SpaceColors
{
    /// <summary>The kinds of file, in palette order. The last is everything else; folders drawn as blocks are "mixed".</summary>
    public static IReadOnlyList<string> TypeLabels { get; } = ["Video", "Audio", "Pictures", "Documents", "Archives", "Code", "Other", "Mixed (small items)"];

    /// <summary>The age groups, newest first.</summary>
    public static IReadOnlyList<string> AgeLabels { get; } = ["Last month", "Last 6 months", "Last year", "Last 2 years", "Over 2 years"];

    /// <summary>The number of colours the mode needs.</summary>
    public static int CategoryCount(ColorMode mode) => mode == ColorMode.Type ? TypeLabels.Count : AgeLabels.Count;

    /// <summary>The label of a category.</summary>
    public static string LabelOf(ColorMode mode, int category) => (mode == ColorMode.Type ? TypeLabels : AgeLabels)[category];

    /// <summary>The group of a node. A folder drawn as one block is "mixed" in type mode and has the age of its newest file in age mode.</summary>
    public static int CategoryOf(SpaceNode node, ColorMode mode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(node);
        return mode == ColorMode.Type ? TypeOf(node) : AgeOf(node.Modified, now);
    }

    private static int TypeOf(SpaceNode node)
    {
        if (node.IsFolder)
        {
            return 7;
        }

        var ext = node.Extension;
        if (FileTypes.IsKind(ext, FileTypeKind.Video))
        {
            return 0;
        }

        if (FileTypes.IsKind(ext, FileTypeKind.Audio))
        {
            return 1;
        }

        if (FileTypes.IsKind(ext, FileTypeKind.Image))
        {
            return 2;
        }

        if (FileTypes.IsKind(ext, FileTypeKind.Document))
        {
            return 3;
        }

        if (FileTypes.IsKind(ext, FileTypeKind.Archive))
        {
            return 4;
        }

        return FileTypes.IsKind(ext, FileTypeKind.Code) ? 5 : 6;
    }

    private static int AgeOf(DateTimeOffset modified, DateTimeOffset now)
    {
        var age = now - modified;
        return age.TotalDays switch
        {
            <= 31 => 0,
            <= 183 => 1,
            <= 366 => 2,
            <= 731 => 3,
            _ => 4,
        };
    }

    /// <summary>
    /// The legend: every group with its total size, largest first, empty groups left out. For type mode <paramref name="byExtension"/> lists the extensions
    /// instead ("a legend of extensions by total size", spec §14), the biggest <paramref name="top"/> and the rest as "other".
    /// </summary>
    public static IReadOnlyList<LegendEntry> Legend(SpaceNode root, ColorMode mode, DateTimeOffset now, bool byExtension = false, int top = 12)
    {
        ArgumentNullException.ThrowIfNull(root);
        var bytes = new Dictionary<int, (long Bytes, int Files)>();
        var extensions = new Dictionary<string, (long Bytes, int Files, int Category)>(StringComparer.Ordinal);
        var stack = new Stack<SpaceNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsFolder)
            {
                foreach (var child in node.Children)
                {
                    stack.Push(child);
                }

                continue;
            }

            var category = CategoryOf(node, mode, now);
            var (b, f) = bytes.GetValueOrDefault(category);
            bytes[category] = (b + node.Size, f + 1);
            if (byExtension)
            {
                var key = node.Extension.Length == 0 ? "(none)" : node.Extension;
                var e = extensions.GetValueOrDefault(key);
                extensions[key] = (e.Bytes + node.Size, e.Files + 1, category);
            }
        }

        if (byExtension && mode == ColorMode.Type)
        {
            var ordered = extensions.OrderByDescending(e => e.Value.Bytes).ToList();
            var shown = ordered.Take(top).Select(e => new LegendEntry(e.Value.Category, e.Key, e.Value.Bytes, e.Value.Files)).ToList();
            var rest = ordered.Skip(top).ToList();
            if (rest.Count > 0)
            {
                shown.Add(new LegendEntry(6, "other extensions", rest.Sum(e => e.Value.Bytes), rest.Sum(e => e.Value.Files)));
            }

            return shown;
        }

        return [.. bytes.OrderByDescending(e => e.Value.Bytes).Select(e => new LegendEntry(e.Key, LabelOf(mode, e.Key), e.Value.Bytes, e.Value.Files))];
    }
}
