using System.Collections.Concurrent;
using Leviathan.Core.Rename;
using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Rename;

/// <summary>Where the media tokens get their facts (the media process in the app); any path it cannot read is left out of the answer.</summary>
public interface IRenameMediaSource
{
    /// <summary>Facts about each of <paramref name="paths"/> that it could read.</summary>
    Task<IReadOnlyDictionary<string, MediaFacts>> GetAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

/// <summary>One line of the preview table.</summary>
/// <param name="Item">The item.</param>
/// <param name="OldName">Its name now.</param>
/// <param name="NewName">The name it would get; the old one when it is left out.</param>
/// <param name="Status">Whether it would be renamed.</param>
/// <param name="Problem">The reason for a conflict or an invalid name.</param>
/// <param name="Included">False when the person unticked it.</param>
public sealed record RenameRow(RenameItem Item, string OldName, string NewName, RenameStatus Status, string? Problem, bool Included);

/// <summary>What the preview shows for one set of steps.</summary>
/// <param name="Rows">One row per item, in order.</param>
/// <param name="Plan">The plan for the ticked items, or null when none is ticked.</param>
public sealed record RenameView(IReadOnlyList<RenameRow> Rows, RenamePlan? Plan)
{
    /// <summary>True when there is something to rename and nothing in the way.</summary>
    public bool CanApply => Plan is { CanApply: true, ChangeCount: > 0 };

    /// <summary>Items that would be renamed.</summary>
    public int ChangeCount => Plan?.ChangeCount ?? 0;

    /// <summary>Items with a problem.</summary>
    public int ProblemCount => Rows.Count(r => r.Included && r.Status is RenameStatus.Conflict or RenameStatus.Invalid);
}

/// <summary>What applying a bulk rename did.</summary>
/// <param name="Renamed">Items that now have their new name.</param>
/// <param name="Problems">One sentence per move that could not be done.</param>
/// <param name="Touched">The new paths, so the folder view can reload and select them.</param>
public sealed record BulkRenameResult(int Renamed, IReadOnlyList<string> Problems, IReadOnlyList<string> Touched);

/// <summary>
/// The state of the bulk-rename dialog (spec §15.3): the items, the ordered steps, which items are ticked, and the preview those make.
/// The preview is computed by <see cref="Compute"/> from a snapshot, so the dialog can do it on the thread pool as the person types and
/// drop answers that are out of date. Applying goes through <see cref="IUndoActions"/> (the worker's journaled restore job, which never
/// overwrites a name), then records one undo entry for the whole batch. No window types, so it is tested directly.
/// </summary>
public sealed class BulkRenameModel
{
    private readonly Func<string, IReadOnlySet<string>>? _siblingsOf;
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _siblings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RenameItem> _items;

    /// <summary>Starts with <paramref name="items"/>, in the order numbering follows. <paramref name="siblingsOf"/> lists the names in a folder (it does I/O, so it is only called from <see cref="Compute"/>, off the UI thread).</summary>
    public BulkRenameModel(IReadOnlyList<RenameItem> items, Func<string, IReadOnlySet<string>>? siblingsOf = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
        _siblingsOf = siblingsOf;
    }

    /// <summary>The steps, in the order they run.</summary>
    public List<RenameDraft> Drafts { get; } = [];

    /// <summary>The items.</summary>
    public IReadOnlyList<RenameItem> Items => _items;

    /// <summary>True when a step uses a media token, so the dialog should fetch media facts.</summary>
    public bool UsesMediaTokens => Drafts.Any(d => d.IsActive && (d.Text.Contains("{taken", StringComparison.OrdinalIgnoreCase)
        || d.Text.Contains("{duration", StringComparison.OrdinalIgnoreCase)
        || d.Text.Contains("{resolution", StringComparison.OrdinalIgnoreCase)
        || d.Text.Contains("{codec", StringComparison.OrdinalIgnoreCase)
        || d.Replace.Contains("{taken", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Ticks or unticks an item. An unticked item keeps its name and is skipped by numbering.</summary>
    public void SetIncluded(string path, bool included)
    {
        if (included)
        {
            _excluded.Remove(path);
        }
        else
        {
            _excluded.Add(path);
        }
    }

    /// <summary>True when the item is ticked.</summary>
    public bool IsIncluded(string path) => !_excluded.Contains(path);

    /// <summary>Gives the items the facts the media tokens need.</summary>
    public void SetMedia(IReadOnlyDictionary<string, MediaFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        _items = [.. _items.Select(i => facts.TryGetValue(i.Path, out var f) ? i with { Media = f } : i)];
    }

    /// <summary>Everything <see cref="Compute"/> needs, copied so the steps can go on being edited while it runs.</summary>
    public RenameInput Snapshot() => new(_items, [.. Drafts.Where(d => d.IsActive).Select(d => d.ToOperation())], new HashSet<string>(_excluded, StringComparer.OrdinalIgnoreCase));

    /// <summary>Builds the preview for <paramref name="input"/>. Pure apart from reading folder listings once per folder; safe off the UI thread.</summary>
    public RenameView Compute(RenameInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var included = input.Items.Where(i => !input.Excluded.Contains(i.Path)).ToList();
        var left = input.Items.Where(i => input.Excluded.Contains(i.Path)).Select(i => (Folder: FolderOf(i), Name: NameOf(i))).ToList();

        IReadOnlySet<string> SiblingsOf(string folder)
        {
            var names = new HashSet<string>(_siblings.GetOrAdd(folder, f => _siblingsOf?.Invoke(f) ?? new HashSet<string>()), StringComparer.OrdinalIgnoreCase);
            names.UnionWith(left.Where(l => string.Equals(l.Folder, folder, StringComparison.OrdinalIgnoreCase)).Select(l => l.Name));
            return names;
        }

        var plan = included.Count == 0 ? null : BulkRename.Plan(included, input.Operations, SiblingsOf);
        var byPath = plan?.Steps.ToDictionary(s => s.Item.Path, StringComparer.OrdinalIgnoreCase);
        var rows = input.Items.Select(i =>
        {
            if (byPath is not null && byPath.TryGetValue(i.Path, out var step))
            {
                return new RenameRow(i, step.OldName, step.NewName, step.Status, step.Problem, true);
            }

            return new RenameRow(i, NameOf(i), NameOf(i), RenameStatus.Unchanged, null, false);
        }).ToList();
        return new RenameView(rows, plan);
    }

    /// <summary>Renames the items of <paramref name="view"/> and records one undo entry for the batch.</summary>
    public static async Task<BulkRenameResult> ApplyAsync(RenameView view, IUndoActions files, Action<IReadOnlyList<ItemMove>> record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(record);
        if (view.Plan is not { CanApply: true } plan)
        {
            return new BulkRenameResult(0, ["There are names in conflict or not allowed; fix them first."], []);
        }

        var moves = plan.Moves();
        if (moves.Count == 0)
        {
            return new BulkRenameResult(0, [], []);
        }

        var result = await files.MoveAsync([.. moves.Select(m => new ItemMove(m.From, m.To))], cancellationToken).ConfigureAwait(false);
        if (result.Done.Count > 0)
        {
            record(result.Done);
        }

        var finals = plan.Steps.Where(s => s.Status == RenameStatus.Ok).Select(s => Path.Combine(Path.GetDirectoryName(s.Item.Path) ?? string.Empty, s.NewName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var renamed = result.Done.Where(d => finals.Contains(d.To)).Select(d => d.To).ToList();
        return new BulkRenameResult(renamed.Count, result.Problems, renamed);
    }

    private static string NameOf(RenameItem item) => Path.GetFileName(item.Path.TrimEnd('\\', '/'));

    private static string FolderOf(RenameItem item) => Path.GetDirectoryName(item.Path.TrimEnd('\\', '/')) ?? string.Empty;
}

/// <summary>A copy of the dialog's state for <see cref="BulkRenameModel.Compute"/>.</summary>
/// <param name="Items">The items.</param>
/// <param name="Operations">The active steps.</param>
/// <param name="Excluded">Unticked item paths.</param>
public sealed record RenameInput(IReadOnlyList<RenameItem> Items, IReadOnlyList<RenameOperation> Operations, IReadOnlySet<string> Excluded);
