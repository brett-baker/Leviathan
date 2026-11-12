namespace Leviathan.Core.Duplicates;

/// <summary>One copy in a group of duplicates or lookalikes, with what a person needs to choose between copies (spec §15.2).</summary>
/// <param name="Path">Full path.</param>
/// <param name="Size">Bytes.</param>
/// <param name="ModifiedUtc">Last write time.</param>
/// <param name="Width">Picture or video width in pixels, or 0 when not known (exact duplicates need none: they are the same).</param>
/// <param name="Height">Picture or video height in pixels, or 0 when not known.</param>
public sealed record ReviewFile(string Path, long Size, DateTime ModifiedUtc, int Width = 0, int Height = 0)
{
    /// <summary>Pixels, 0 when unknown.</summary>
    public long Pixels => (long)Width * Height;

    /// <summary>The name with its extension.</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The folder it is in.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>Which copy of a group to keep (spec §15.2 auto-select rules).</summary>
public enum KeepRule
{
    /// <summary>The most pixels; ties go to the larger file, then the newer one.</summary>
    HighestResolution,

    /// <summary>The largest file.</summary>
    Largest,

    /// <summary>The most recently modified.</summary>
    Newest,

    /// <summary>The least recently modified (the original, usually).</summary>
    Oldest,

    /// <summary>The copy inside a preferred folder; when several or none are in it, the other rules break the tie.</summary>
    PreferredFolder,
}

/// <summary>
/// Chooses the copy to keep in each group (spec §15.2). Every rule ends the same way when it cannot decide: the copy found first
/// wins, so the same input always gives the same answer and re-running a rule never flips a choice.
/// </summary>
public static class ReviewRules
{
    /// <summary>The copy to keep from <paramref name="group"/>. With <see cref="KeepRule.PreferredFolder"/>, <paramref name="preferredFolder"/> is the folder (or its subfolders) to favour.</summary>
    public static ReviewFile Keep(IReadOnlyList<ReviewFile> group, KeepRule rule, string? preferredFolder = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Count == 0)
        {
            throw new ArgumentException("A group has at least one file.", nameof(group));
        }

        IEnumerable<ReviewFile> pool = group;
        if (rule == KeepRule.PreferredFolder)
        {
            var inside = string.IsNullOrWhiteSpace(preferredFolder) ? [] : group.Where(f => IsInside(f.Path, preferredFolder)).ToList();
            if (inside.Count > 0)
            {
                pool = inside;
            }

            // The rest of the order decides among copies in (or all outside) the folder: the oldest is most likely the original.
            return pool.OrderBy(f => f.ModifiedUtc).First();
        }

        return rule switch
        {
            KeepRule.HighestResolution => pool.OrderByDescending(f => f.Pixels).ThenByDescending(f => f.Size).ThenByDescending(f => f.ModifiedUtc).First(),
            KeepRule.Largest => pool.OrderByDescending(f => f.Size).ThenByDescending(f => f.Pixels).ThenByDescending(f => f.ModifiedUtc).First(),
            KeepRule.Newest => pool.OrderByDescending(f => f.ModifiedUtc).First(),
            KeepRule.Oldest => pool.OrderBy(f => f.ModifiedUtc).First(),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
    }

    /// <summary>The copies to act on once <see cref="Keep"/> has chosen: everything but the kept one, in group order.</summary>
    public static IReadOnlyList<ReviewFile> Extras(IReadOnlyList<ReviewFile> group, KeepRule rule, string? preferredFolder = null)
    {
        var keep = Keep(group, rule, preferredFolder);
        return [.. group.Where(f => !ReferenceEquals(f, keep))];
    }

    private static bool IsInside(string path, string folder)
    {
        var prefix = folder.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase);
    }
}
