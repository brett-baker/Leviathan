using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// Puts items back where they came from (spec §12.5): the other half of undo for a rename (the new name back to the old one) and
/// for a delete (out of the Recycle Bin or a share's recycle folder). It never overwrites: if something already has the old name
/// the item stays where it is and the user is told, so undo cannot destroy what was made since.
/// </summary>
public sealed class RestoreExecutor : IJobExecutor
{
    private readonly IFileSystem _fs;
    private readonly BinRegistry? _bin;

    /// <summary>Creates the executor.</summary>
    /// <param name="fileSystem">The disk.</param>
    /// <param name="bin">When given, a restored item's line is taken out of its trash folder's manifest, so the Soft-delete bin does not list it again.</param>
    public RestoreExecutor(IFileSystem fileSystem, BinRegistry? bin = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fs = fileSystem;
        _bin = bin;
    }

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.Restore;

    /// <inheritdoc />
    public Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var targets = request.Targets ?? [];
        var failures = new List<string>();
        for (var i = 0; i < request.Sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = LocationPath.Parse(request.Sources[i]);
            var target = LocationPath.Parse(targets[i]);
            context.Report(0, -1, i, request.Sources.Count, source.Name);
            ItemOutcome outcome;
            try
            {
                outcome = RestoreOne(source, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome = new ItemOutcome(source.Value, null, ItemStatus.Failed, ex.Message);
            }

            context.AddResult(outcome);
            if (outcome.Status == ItemStatus.Failed)
            {
                failures.Add($"{target.Name}: {outcome.Error}");
            }
        }

        context.Report(0, -1, request.Sources.Count, request.Sources.Count);
        return Task.FromResult(failures.Count == 0
            ? JobOutcome.Completed
            : new JobOutcome(JobState.Failed, $"{failures.Count} of {request.Sources.Count} item(s) could not be restored. {string.Join(" ", failures.Take(3))}"));
    }

    private static string Io(LocationPath path) => path.ToWin32Path();

    private ItemOutcome RestoreOne(LocationPath source, LocationPath target)
    {
        var isFolder = _fs.Directory.Exists(Io(source));
        if (!isFolder && !_fs.File.Exists(Io(source)))
        {
            return new ItemOutcome(source.Value, null, ItemStatus.Failed, $"{source.Name} is no longer there.");
        }

        var folder = target.Parent;
        if (folder is null)
        {
            return new ItemOutcome(source.Value, null, ItemStatus.Failed, "There is no folder to put it in.");
        }

        // Only a change of letter case is the same item; anything else already there is somebody else's.
        var onlyCase = string.Equals(source.Value, target.Value, StringComparison.OrdinalIgnoreCase);
        if (!onlyCase && (_fs.File.Exists(Io(target)) || _fs.Directory.Exists(Io(target))))
        {
            return new ItemOutcome(source.Value, null, ItemStatus.Failed, $"Something named '{target.Name}' already exists in {folder.Name}.");
        }

        _fs.Directory.CreateDirectory(Io(folder));
        if (isFolder)
        {
            _fs.Directory.Move(Io(source), Io(target));
        }
        else
        {
            _fs.File.Move(Io(source), Io(target));
        }

        // Explorer's Restore removes the $I record as well; leaving it would list a ghost in the Recycle Bin.
        if (RecycleBinIndex.IsBinItem(_fs, source, out var info) && _fs.File.Exists(info))
        {
            try
            {
                _fs.File.Delete(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The item is back; a stale record is harmless and Windows cleans it up.
            }
        }

        // The item left the bin: its manifest line goes too, whether it came from our trash folder or a NAS bin we recorded.
        if (!onlyCase)
        {
            _bin?.Forget([source]);
        }

        return new ItemOutcome(source.Value, target.Value, ItemStatus.Done);
    }
}
