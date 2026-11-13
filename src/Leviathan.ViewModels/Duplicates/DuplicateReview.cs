using Leviathan.Contracts;
using Leviathan.Core.Duplicates;

namespace Leviathan.ViewModels.Duplicates;

/// <summary>One group in the results window: the copies and the one that will be kept.</summary>
public sealed class ReviewGroup
{
    private readonly List<ReviewFile> _files;

    /// <summary>Makes a group of two or more copies; <paramref name="keep"/> must be one of them.</summary>
    public ReviewGroup(string key, IReadOnlyList<ReviewFile> files, bool exact, ReviewFile keep)
    {
        ArgumentNullException.ThrowIfNull(files);
        Key = key;
        _files = [.. files];
        Exact = exact;
        Keep = keep;
    }

    /// <summary>Identifies the group (for exact duplicates, size and hash).</summary>
    public string Key { get; }

    /// <summary>True when the copies are identical, false for lookalikes (spec §15.2).</summary>
    public bool Exact { get; }

    /// <summary>The copies, in the order they were found.</summary>
    public IReadOnlyList<ReviewFile> Files => _files;

    /// <summary>The copy that will be kept.</summary>
    public ReviewFile Keep { get; set; }

    /// <summary>The copies that will be acted on.</summary>
    public IEnumerable<ReviewFile> Extras => _files.Where(f => !ReferenceEquals(f, Keep));

    /// <summary>Bytes the extra copies take.</summary>
    public long ExtraBytes => Extras.Sum(f => f.Size);

    internal bool Remove(IReadOnlySet<string> paths)
    {
        _files.RemoveAll(f => paths.Contains(f.Path));
        if (!_files.Contains(Keep) && _files.Count > 0)
        {
            Keep = _files[0];
        }

        return _files.Count >= 2;
    }
}

/// <summary>Finds duplicate files for the window; the real one runs the worker's find-duplicates job.</summary>
public interface IDuplicateScanner
{
    /// <summary>Looks under <paramref name="roots"/> and returns the groups. Honours <paramref name="cancellationToken"/>.</summary>
    Task<DuplicateScan> ScanAsync(IReadOnlyList<string> roots, long minimumBytes, IReadOnlyList<string>? extensions, bool includeHidden, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>Finds look-alike pictures and videos for the window (spec §15.2); the real one asks the media process for signatures.</summary>
public interface ISimilarMediaScanner
{
    /// <summary>
    /// Looks under <paramref name="roots"/> for pictures (and videos when <paramref name="includeVideos"/>) that look alike.
    /// <paramref name="tolerance"/> is how many of 64 hash bits may differ: 0 is the same picture, 6 catches resized and re-compressed copies.
    /// </summary>
    Task<DuplicateScan> ScanAsync(IReadOnlyList<string> roots, bool includeVideos, int tolerance, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>What a scan found.</summary>
/// <param name="Groups">The groups, the ones that waste most first.</param>
/// <param name="Notes">What the search could not read, and the like, for the person to see.</param>
public sealed record DuplicateScan(IReadOnlyList<ReviewGroup> Groups, IReadOnlyList<string> Notes);

/// <summary>
/// The state of the duplicate results window (spec §15.2): the groups, the rule that picks the copy to keep in each, and what is
/// left to clean up. No window types, so it is tested directly.
/// </summary>
public sealed class DuplicateReview
{
    private readonly List<ReviewGroup> _groups;

    /// <summary>Starts with <paramref name="groups"/> and applies <see cref="KeepRule.Oldest"/> (the original, usually).</summary>
    public DuplicateReview(IReadOnlyList<ReviewGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        _groups = [.. groups];
        Apply(KeepRule.Oldest, null);
    }

    /// <summary>The groups still to decide about.</summary>
    public IReadOnlyList<ReviewGroup> Groups => _groups;

    /// <summary>The rule last applied.</summary>
    public KeepRule Rule { get; private set; }

    /// <summary>The preferred folder last used.</summary>
    public string? PreferredFolder { get; private set; }

    /// <summary>Copies that would be acted on.</summary>
    public int ExtraCount => _groups.Sum(g => g.Files.Count - 1);

    /// <summary>Bytes those copies take.</summary>
    public long ExtraBytes => _groups.Sum(g => g.ExtraBytes);

    /// <summary>Picks the copy to keep in every group by <paramref name="rule"/>.</summary>
    public void Apply(KeepRule rule, string? preferredFolder)
    {
        Rule = rule;
        PreferredFolder = preferredFolder;
        foreach (var group in _groups)
        {
            group.Keep = ReviewRules.Keep(group.Files, rule, preferredFolder);
        }
    }

    /// <summary>The groups as the clean-up service wants them.</summary>
    public IReadOnlyList<CleanupGroup> ToCleanup() => [.. _groups.Select(g => new CleanupGroup(g.Files, g.Keep, g.Exact))];

    /// <summary>Drops files that were acted on; a group left with one copy has nothing more to decide and goes.</summary>
    public void Resolve(IEnumerable<string> paths)
    {
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _groups.RemoveAll(g => !g.Remove(set));
    }

    /// <summary>
    /// Makes look-alike groups from sets of files (lookalikes are never <see cref="ReviewGroup.Exact"/>, so no hard link is offered).
    /// Sets of fewer than two files are dropped; the groups that would free most come first.
    /// </summary>
    public static IReadOnlyList<ReviewGroup> SimilarGroupsFrom(IEnumerable<IReadOnlyList<ReviewFile>> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var groups = new List<ReviewGroup>();
        var n = 0;
        foreach (var set in sets.Where(s => s.Count >= 2))
        {
            groups.Add(new ReviewGroup("similar:" + ++n, set, exact: false, set[0]));
        }

        return [.. groups.OrderByDescending(g => g.ExtraBytes).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Turns the worker's per-file results into groups: files with the same result key are copies. The key is <c>size:hash</c>, so the
    /// size comes from it; <paramref name="modified"/> reads a file's last write time (the real one does it off the UI thread).
    /// </summary>
    public static IReadOnlyList<ReviewGroup> GroupsFrom(IReadOnlyList<ItemOutcome> items, Func<string, DateTime> modified)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(modified);
        var groups = new List<ReviewGroup>();
        foreach (var group in items.Where(i => i is { Status: ItemStatus.Done, Result: not null }).GroupBy(i => i.Result!, StringComparer.Ordinal))
        {
            var colon = group.Key.IndexOf(':');
            var size = colon > 0 && long.TryParse(group.Key.AsSpan(0, colon), out var s) ? s : 0;
            var files = group.Select(i => new ReviewFile(i.Source, size, modified(i.Source))).ToList();
            if (files.Count >= 2)
            {
                groups.Add(new ReviewGroup(group.Key, files, exact: true, files[0]));
            }
        }

        return [.. groups.OrderByDescending(g => g.ExtraBytes).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase)];
    }
}
