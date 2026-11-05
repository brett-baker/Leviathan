using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// Deletes soft-deleted items for good (spec §12.5): the ones the user picked in the Soft-delete bin, or, when the request carries a
/// <see cref="PurgePolicy"/>, whatever the age and size limits say. It only ever touches things inside a recycle folder, so a wrong
/// path in a request cannot delete a live file. Each item is reported, and the manifest line goes with it.
/// </summary>
public sealed class PurgeExecutor : IJobExecutor
{
    private readonly IFileSystem _fs;
    private readonly BinRegistry _registry;
    private readonly BinScanner _scanner;
    private readonly TimeProvider _time;

    /// <summary>Creates the executor.</summary>
    public PurgeExecutor(IFileSystem fileSystem, BinRegistry registry, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(registry);
        _fs = fileSystem;
        _registry = registry;
        _scanner = new BinScanner(registry);
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.Purge;

    /// <inheritdoc />
    public Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        IReadOnlyList<LocationPath> targets;
        if (request.PurgePolicy is { } policy)
        {
            var scan = _scanner.Scan(measureFolders: true, cancellationToken);
            targets = [.. BinRetention.Expired(scan.Items, _time.GetUtcNow(), policy.MaxAgeDays, policy.MaxBytes).Select(i => i.Trashed)];
            foreach (var root in scan.Unreachable)
            {
                context.AddNote($"{root.Root} could not be reached, so its deleted items were not checked.");
            }

            if (targets.Count == 0)
            {
                context.AddNote("Nothing in the bin is old enough or over the size limit.");
            }
        }
        else
        {
            targets = [.. request.Sources.Select(LocationPath.Parse)];
        }

        var failures = new List<string>();
        var purged = new List<LocationPath>();
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            context.Report(0, -1, i, targets.Count, target.Name);
            ItemOutcome outcome;
            try
            {
                outcome = PurgeOne(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome = new ItemOutcome(target.Value, null, ItemStatus.Failed, ex.Message);
            }

            context.AddResult(outcome);
            if (outcome.Status == ItemStatus.Done)
            {
                purged.Add(target);
            }
            else if (outcome.Status == ItemStatus.Failed)
            {
                failures.Add($"{target.Name}: {outcome.Error}");
            }
        }

        _registry.Forget(purged);
        context.Report(0, -1, targets.Count, targets.Count);
        return Task.FromResult(failures.Count == 0
            ? JobOutcome.Completed
            : new JobOutcome(JobState.Failed, $"{failures.Count} of {targets.Count} item(s) could not be purged. {string.Join(" ", failures.Take(3))}"));
    }

    private ItemOutcome PurgeOne(LocationPath target)
    {
        // Never delete outside a recycle folder, and never a recycle folder itself: this job exists to empty the bin, not to be a second Shift+Delete.
        if (!RecycleFolders.IsInsideBin(target) || IsBinFolder(target))
        {
            return new ItemOutcome(target.Value, null, ItemStatus.Failed, $"{target.Name} is not a deleted item, so it was left alone.");
        }

        var isFolder = _fs.Directory.Exists(target.ToWin32Path());
        if (!isFolder && !_fs.File.Exists(target.ToWin32Path()))
        {
            return new ItemOutcome(target.Value, null, ItemStatus.Skipped, $"{target.Name} was already gone.");
        }

        PermanentDelete.Run(_fs, target.ToWin32Path(), isFolder);
        RemoveEmptyBatchFolder(target);
        return new ItemOutcome(target.Value, null, ItemStatus.Done);
    }

    private static bool IsBinFolder(LocationPath path) =>
        path.Name.Equals(RecycleFolders.TrashFolder, StringComparison.OrdinalIgnoreCase)
        || path.Name.Equals("#recycle", StringComparison.OrdinalIgnoreCase)
        || path.Name.Equals("@Recycle", StringComparison.OrdinalIgnoreCase);

    // Leviathan's own deletes go into a folder per batch (20261103-103000-ab12cd); when its last item is purged the empty folder goes too.
    private void RemoveEmptyBatchFolder(LocationPath purged)
    {
        var batch = purged.Parent;
        if (batch?.Parent is not { } root || !root.Name.Equals(RecycleFolders.TrashFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (_fs.Directory.Exists(batch.ToWin32Path()) && !_fs.Directory.EnumerateFileSystemEntries(batch.ToWin32Path()).Any())
            {
                _fs.Directory.Delete(batch.ToWin32Path());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind is harmless.
        }
    }
}
