namespace Leviathan.Core.Compare;

/// <summary>How two folders are brought into line (spec §15.5).</summary>
public enum SyncMode
{
    /// <summary>The right folder becomes a copy of the left: new and changed items are copied across, and what only the right has is deleted.</summary>
    MirrorLeftToRight,

    /// <summary>The left folder becomes a copy of the right.</summary>
    MirrorRightToLeft,

    /// <summary>Both folders receive what only the other has and the newer of a changed pair; a pair with no newer side is flagged as a conflict. Nothing is deleted.</summary>
    TwoWay,

    /// <summary>New and newer items are copied from left to right; nothing is ever deleted or overwritten with an older file.</summary>
    UpdateLeftToRight,

    /// <summary>New and newer items are copied from right to left; nothing is ever deleted or overwritten with an older file.</summary>
    UpdateRightToLeft,
}

/// <summary>What the person chose for one path, overriding the mode (spec §15.5 "per-item action overrides").</summary>
public enum SyncChoice
{
    /// <summary>Whatever the mode says.</summary>
    Default,

    /// <summary>Leave this item as it is.</summary>
    Skip,

    /// <summary>Make the right item match the left one (copy it across, or delete the right one when the left has none).</summary>
    LeftToRight,

    /// <summary>Make the left item match the right one.</summary>
    RightToLeft,
}

/// <summary>One step of a sync.</summary>
public enum SyncActionKind
{
    /// <summary>Copy the left item over to the right folder.</summary>
    CopyToRight,

    /// <summary>Copy the right item over to the left folder.</summary>
    CopyToLeft,

    /// <summary>Delete the right item (soft delete: the Recycle Bin or the share's recycle folder).</summary>
    DeleteRight,

    /// <summary>Delete the left item (soft delete).</summary>
    DeleteLeft,
}

/// <summary>One step of a sync plan.</summary>
/// <param name="Path">The path below the two folders.</param>
/// <param name="Kind">What is done.</param>
/// <param name="IsDirectory">True for a folder (a copied folder takes everything in it along).</param>
/// <param name="Size">Bytes copied by this step (0 for deletions; for a copied folder the size of what it holds as far as the comparison knows).</param>
/// <param name="Replaces">True when the step overwrites an existing item.</param>
/// <param name="OverwritesNewer">True when the step overwrites an item that is newer than the one copied: a mirror does that on purpose, and the preview warns about it.</param>
/// <param name="Reason">Why, in words for the preview: "only on the left", "newer on the left".</param>
public sealed record SyncAction(string Path, SyncActionKind Kind, bool IsDirectory, long Size, bool Replaces, bool OverwritesNewer, string Reason);

/// <summary>What a sync would do (spec §15.5: "a dry-run preview is always shown first"). Nothing here has been done.</summary>
/// <param name="Mode">The mode it was planned for.</param>
/// <param name="Actions">The steps in the order they run: deletions first (they free space and clear the way for a changed type), then copies by path.</param>
/// <param name="Conflicts">Pairs the mode cannot settle on its own (two-way, same time, different contents); they are left alone unless the person chooses a side.</param>
/// <param name="Notes">Words about the plan, such as that a two-way sync never deletes.</param>
public sealed record SyncPlan(SyncMode Mode, IReadOnlyList<SyncAction> Actions, IReadOnlyList<CompareEntry> Conflicts, IReadOnlyList<string> Notes)
{
    /// <summary>Steps of the kinds <paramref name="kinds"/>.</summary>
    public IEnumerable<SyncAction> Of(params SyncActionKind[] kinds) => Actions.Where(a => kinds.Contains(a.Kind));

    /// <summary>How many bytes are copied in all.</summary>
    public long BytesToCopy => Actions.Where(a => a.Kind is SyncActionKind.CopyToLeft or SyncActionKind.CopyToRight).Sum(a => a.Size);

    /// <summary>How many items are deleted.</summary>
    public int Deletions => Actions.Count(a => a.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight);

    /// <summary>How many steps overwrite a newer item.</summary>
    public int NewerOverwritten => Actions.Count(a => a.OverwritesNewer);

    /// <summary>True when there is nothing to do.</summary>
    public bool IsEmpty => Actions.Count == 0;
}

/// <summary>
/// Turns a <see cref="CompareResult"/> into the steps of a sync (spec §15.5). Pure: it decides, never touches a file, so the preview the
/// person confirms is exactly the list of steps that then run. A folder that is copied or deleted takes its contents with it, so the
/// items inside are not listed again. Per-item choices override the mode.
/// </summary>
public static class SyncPlanner
{
    /// <summary>Plans <paramref name="mode"/> over <paramref name="result"/>, with <paramref name="choices"/> (by path, case-insensitive) overriding it.</summary>
    public static SyncPlan Plan(CompareResult result, SyncMode mode, IReadOnlyDictionary<string, SyncChoice>? choices = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var chosen = new Dictionary<string, SyncChoice>(choices ?? new Dictionary<string, SyncChoice>(), StringComparer.OrdinalIgnoreCase);
        var deletions = new List<SyncAction>();
        var copies = new List<SyncAction>();
        var conflicts = new List<CompareEntry>();
        foreach (var entry in result.Entries)
        {
            var choice = chosen.GetValueOrDefault(entry.Path);
            if (choice == SyncChoice.Skip || entry.Status == CompareStatus.Identical && choice == SyncChoice.Default)
            {
                continue;
            }

            var steps = choice == SyncChoice.Default ? ForMode(entry, mode, conflicts) : Forced(entry, choice);
            foreach (var step in steps)
            {
                (step.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight ? deletions : copies).Add(step);
            }
        }

        // A folder copied or deleted takes what is inside it: those steps would only repeat it.
        var actions = new List<SyncAction>();
        actions.AddRange(Collapse(deletions).OrderByDescending(a => a.Path.Count(c => c == '\\')).ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase));
        actions.AddRange(Collapse(copies).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase));

        var notes = new List<string>();
        if (mode == SyncMode.TwoWay)
        {
            notes.Add("A two-way sync never deletes: an item missing on one side is copied there, not removed from the other.");
        }

        if (mode is SyncMode.UpdateLeftToRight or SyncMode.UpdateRightToLeft)
        {
            notes.Add("An update only copies new and newer items; nothing is deleted and nothing newer is overwritten.");
        }

        return new SyncPlan(mode, actions, conflicts, notes);
    }

    private static IEnumerable<SyncAction> ForMode(CompareEntry e, SyncMode mode, List<CompareEntry> conflicts)
    {
        switch (mode)
        {
            case SyncMode.MirrorLeftToRight:
                return Mirror(e, toRight: true);
            case SyncMode.MirrorRightToLeft:
                return Mirror(e, toRight: false);
            case SyncMode.UpdateLeftToRight:
                return e.Status is CompareStatus.LeftOnly or CompareStatus.NewerLeft ? [Copy(e, toRight: true)] : [];
            case SyncMode.UpdateRightToLeft:
                return e.Status is CompareStatus.RightOnly or CompareStatus.NewerRight ? [Copy(e, toRight: false)] : [];
            default:
                switch (e.Status)
                {
                    case CompareStatus.LeftOnly or CompareStatus.NewerLeft:
                        return [Copy(e, toRight: true)];
                    case CompareStatus.RightOnly or CompareStatus.NewerRight:
                        return [Copy(e, toRight: false)];
                    default:
                        conflicts.Add(e);
                        return [];
                }
        }
    }

    // The target becomes a copy of the source: copy what is new or changed (even over a newer file), delete what the source lacks.
    private static IEnumerable<SyncAction> Mirror(CompareEntry e, bool toRight)
    {
        switch (e.Status)
        {
            case CompareStatus.LeftOnly:
                return toRight ? [Copy(e, true)] : [Delete(e, toRight: false)];
            case CompareStatus.RightOnly:
                return toRight ? [Delete(e, toRight: true)] : [Copy(e, false)];
            case CompareStatus.TypeMismatch:
                return [Delete(e, toRight), Copy(e, toRight, replaces: false)];
            default:
                return [Copy(e, toRight)];
        }
    }

    // A person's choice for one path: make that side's item the truth.
    private static IEnumerable<SyncAction> Forced(CompareEntry e, SyncChoice choice)
    {
        var toRight = choice == SyncChoice.LeftToRight;
        var source = toRight ? e.Left : e.Right;
        var target = toRight ? e.Right : e.Left;
        if (source is null)
        {
            return target is null ? [] : [Delete(e, toRight)];
        }

        if (e.Status == CompareStatus.TypeMismatch)
        {
            return [Delete(e, toRight), Copy(e, toRight, replaces: false)];
        }

        return e.Status == CompareStatus.Identical ? [] : [Copy(e, toRight)];
    }

    // For a file against a folder the entry's flag is the left one's; the right one is the opposite.
    private static bool SourceIsDirectory(CompareEntry e, bool toRight) => e.Status == CompareStatus.TypeMismatch ? (toRight ? e.IsDirectory : !e.IsDirectory) : e.IsDirectory;

    private static SyncAction Copy(CompareEntry e, bool toRight, bool? replaces = null)
    {
        var source = toRight ? e.Left : e.Right;
        var target = toRight ? e.Right : e.Left;
        var replace = replaces ?? target is not null;
        var newer = target is { } t && source is { } s && t.Modified > s.Modified;
        var reason = target is null
            ? $"only on the {(toRight ? "left" : "right")}"
            : e.Status is CompareStatus.NewerLeft or CompareStatus.NewerRight
                ? (newer ? $"the {(toRight ? "right" : "left")} one is newer, but the mirror makes it match" : $"newer on the {(toRight ? "left" : "right")}")
                : "different contents";
        return new SyncAction(e.Path, toRight ? SyncActionKind.CopyToRight : SyncActionKind.CopyToLeft, SourceIsDirectory(e, toRight), source?.Size ?? 0, replace, newer, reason);
    }

    private static SyncAction Delete(CompareEntry e, bool toRight)
    {
        var isDirectory = e.Status == CompareStatus.TypeMismatch ? (toRight ? !e.IsDirectory : e.IsDirectory) : e.IsDirectory;
        var reason = e.Status == CompareStatus.TypeMismatch ? "a file on one side and a folder on the other" : $"only on the {(toRight ? "right" : "left")}";
        return new SyncAction(e.Path, toRight ? SyncActionKind.DeleteRight : SyncActionKind.DeleteLeft, isDirectory, 0, false, false, reason);
    }

    // Drops steps below a folder that is already copied (or deleted) in the same direction; a copied folder counts the bytes of what it takes along.
    private static List<SyncAction> Collapse(List<SyncAction> steps)
    {
        var folders = steps.Where(s => s.IsDirectory && !s.Replaces).ToList();
        var result = new List<SyncAction>();
        foreach (var step in steps)
        {
            if (folders.Any(f => f.Kind == step.Kind && step.Path.StartsWith(f.Path + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (step.IsDirectory && !step.Replaces)
            {
                var inside = steps.Where(o => o.Kind == step.Kind && o.Path.StartsWith(step.Path + "\\", StringComparison.OrdinalIgnoreCase)).Sum(o => o.Size);
                result.Add(step with { Size = step.Size + inside });
            }
            else
            {
                result.Add(step);
            }
        }

        return result;
    }
}
