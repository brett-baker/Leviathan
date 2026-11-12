using Leviathan.Core.Duplicates;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Duplicates;

/// <summary>What to do with the extra copies of a group (spec §15.2 actions).</summary>
public enum CleanupKind
{
    /// <summary>Soft delete: the Recycle Bin, or a share's recycle folder (spec §12.5). Undoable.</summary>
    Delete,

    /// <summary>Move to a folder, names kept unique.</summary>
    Move,

    /// <summary>Replace each extra copy with a hard link to the kept one: same contents, no extra space. Exact duplicates on one volume only.</summary>
    HardLink,

    /// <summary>Put a tag on each extra copy and leave it where it is.</summary>
    Tag,
}

/// <summary>A group the person has decided about: the files and the one to keep.</summary>
/// <param name="Files">Every copy, the kept one included.</param>
/// <param name="Keep">The copy to leave alone; the others are the ones acted on.</param>
/// <param name="Exact">True when the files are byte-for-byte equal (the exact finder), which a hard link needs; lookalikes are not.</param>
public sealed record CleanupGroup(IReadOnlyList<ReviewFile> Files, ReviewFile Keep, bool Exact);

/// <summary>What a clean-up did.</summary>
/// <param name="Done">Copies acted on.</param>
/// <param name="Bytes">Their size added up: the space freed by Delete and HardLink, the amount moved by Move, 0 for Tag.</param>
/// <param name="Problems">One sentence for each copy that could not be done; the others went ahead.</param>
/// <param name="Undo">Puts back what was done, or null when nothing was.</param>
/// <param name="DonePaths">The original paths of the copies that were acted on, so a window can drop them from its list.</param>
public sealed record CleanupResult(int Done, long Bytes, IReadOnlyList<string> Problems, Func<Task>? Undo, IReadOnlyList<string>? DonePaths = null);

/// <summary>
/// Acts on the extra copies of duplicate groups (spec §15.2). Never touches a kept copy, and never a file that is the kept copy of any
/// other group, so overlapping scopes cannot remove the last copy of something. Delete goes through the journaled soft delete, so
/// nothing is lost for good; every action returns an undo that does the opposite. A copy that fails is reported and the rest go on.
/// </summary>
/// <remarks>
/// <paramref name="recordDelete"/> hears about every soft delete so the app's Ctrl+Z (spec §12.5) can undo it after the window is gone.
/// </remarks>
public sealed class DuplicateCleanup(IUndoActions files, ITagStore tags, IHardLinker? links = null, Action<IReadOnlyList<ItemMove>>? recordDelete = null)
{
    /// <summary>The copies <paramref name="groups"/> would act on, in group order, with the kept copy of each.</summary>
    public static IReadOnlyList<(ReviewFile Extra, ReviewFile Keep, bool Exact)> Plan(IReadOnlyList<CleanupGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var protectedPaths = groups.Select(g => g.Keep.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(ReviewFile, ReviewFile, bool)>();
        foreach (var group in groups)
        {
            foreach (var file in group.Files)
            {
                if (!string.Equals(file.Path, group.Keep.Path, StringComparison.OrdinalIgnoreCase) && !protectedPaths.Contains(file.Path) && seen.Add(file.Path))
                {
                    plan.Add((file, group.Keep, group.Exact));
                }
            }
        }

        return plan;
    }

    /// <summary>Runs <paramref name="kind"/> on the planned copies. <paramref name="moveFolder"/> is needed for Move, <paramref name="tagId"/> for Tag.</summary>
    public async Task<CleanupResult> RunAsync(IReadOnlyList<CleanupGroup> groups, CleanupKind kind, string? moveFolder, int tagId, CancellationToken cancellationToken)
    {
        var plan = Plan(groups);
        if (plan.Count == 0)
        {
            return new CleanupResult(0, 0, [], null);
        }

        return kind switch
        {
            CleanupKind.Delete => await DeleteAsync(plan, cancellationToken).ConfigureAwait(false),
            CleanupKind.Move => await MoveAsync(plan, moveFolder, cancellationToken).ConfigureAwait(false),
            CleanupKind.HardLink => await LinkAsync(plan, cancellationToken).ConfigureAwait(false),
            CleanupKind.Tag => await TagAsync(plan, tagId, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private async Task<CleanupResult> DeleteAsync(IReadOnlyList<(ReviewFile Extra, ReviewFile Keep, bool Exact)> plan, CancellationToken cancellationToken)
    {
        var result = await files.DeleteAsync([.. plan.Select(p => p.Extra.Path)], cancellationToken).ConfigureAwait(false);
        if (result.Done.Count > 0)
        {
            recordDelete?.Invoke(result.Done);
        }

        var bytes = SizeOf(plan, result.Done.Select(d => d.From));
        return new CleanupResult(result.Done.Count, bytes, result.Problems, UndoMoves(result.Done), [.. result.Done.Select(d => d.From)]);
    }

    private async Task<CleanupResult> MoveAsync(IReadOnlyList<(ReviewFile Extra, ReviewFile Keep, bool Exact)> plan, string? folder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new CleanupResult(0, 0, ["Choose a folder to move the extra copies to."], null);
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<ItemMove>();
        foreach (var (extra, _, _) in plan)
        {
            moves.Add(new ItemMove(extra.Path, UniqueTarget(folder, extra.Name, taken)));
        }

        var result = await files.MoveAsync(moves, cancellationToken).ConfigureAwait(false);
        return new CleanupResult(result.Done.Count, SizeOf(plan, result.Done.Select(d => d.From)), result.Problems, UndoMoves(result.Done), [.. result.Done.Select(d => d.From)]);
    }

    private async Task<CleanupResult> LinkAsync(IReadOnlyList<(ReviewFile Extra, ReviewFile Keep, bool Exact)> plan, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var linked = new List<(ReviewFile Extra, ReviewFile Keep, bool Exact)>();
        if (links is null)
        {
            return new CleanupResult(0, 0, ["Hard links are not available here."], null);
        }

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Exact)
            {
                problems.Add($"'{item.Extra.Name}' is only similar to the kept copy, not identical: it was not linked.");
                continue;
            }

            if (!SameVolume(item.Extra.Path, item.Keep.Path))
            {
                problems.Add($"'{item.Extra.Name}' is on another drive or share than the kept copy: a hard link needs one volume.");
                continue;
            }

            var problem = await links.ReplaceWithLinkAsync(item.Keep.Path, item.Extra.Path, cancellationToken).ConfigureAwait(false);
            if (problem is null)
            {
                linked.Add(item);
            }
            else
            {
                problems.Add(problem);
            }
        }

        if (linked.Count == 0)
        {
            return new CleanupResult(0, 0, problems, null);
        }

        return new CleanupResult(linked.Count, linked.Sum(l => l.Extra.Size), problems, DonePaths: [.. linked.Select(l => l.Extra.Path)], Undo: async () =>
        {
            foreach (var (extra, _, _) in linked)
            {
                if (await links.MakeIndependentAsync(extra.Path, CancellationToken.None).ConfigureAwait(false) is { } failed)
                {
                    throw new IOException(failed);
                }
            }
        });
    }

    private async Task<CleanupResult> TagAsync(IReadOnlyList<(ReviewFile Extra, ReviewFile Keep, bool Exact)> plan, int tagId, CancellationToken cancellationToken)
    {
        var before = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var (extra, _, _) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = await tags.GetAsync(extra.Path, cancellationToken).ConfigureAwait(false);
                if (TagFiles.IsGone(current))
                {
                    problems.Add($"'{extra.Name}' is no longer there.");
                    continue;
                }

                await tags.SetAsync(extra.Path, new HashSet<int>(current) { tagId }, cancellationToken).ConfigureAwait(false);
                before[extra.Path] = current;
            }
            catch (IOException ex)
            {
                problems.Add($"'{extra.Name}' could not be tagged: {ex.Message}");
            }
        }

        if (before.Count == 0)
        {
            return new CleanupResult(0, 0, problems, null);
        }

        return new CleanupResult(before.Count, 0, problems, DonePaths: [.. before.Keys], Undo: async () =>
        {
            foreach (var (path, tagsBefore) in before)
            {
                await tags.SetAsync(path, tagsBefore, CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    private Func<Task>? UndoMoves(IReadOnlyList<ItemMove> done)
    {
        if (done.Count == 0)
        {
            return null;
        }

        return async () =>
        {
            var back = await files.MoveAsync([.. done.Select(d => new ItemMove(d.To, d.From))], CancellationToken.None).ConfigureAwait(false);
            if (back.Problems.Count > 0)
            {
                throw new IOException(string.Join(" ", back.Problems));
            }
        };
    }

    private static long SizeOf(IReadOnlyList<(ReviewFile Extra, ReviewFile Keep, bool Exact)> plan, IEnumerable<string> done)
    {
        var doneSet = done.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.Where(p => doneSet.Contains(p.Extra.Path)).Sum(p => p.Extra.Size);
    }

    private static string UniqueTarget(string folder, string name, HashSet<string> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = Path.Combine(folder, name);
        for (var n = 2; !taken.Add(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
        }

        return candidate;
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
}
