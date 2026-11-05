using System.IO.Abstractions;
using System.Text.Json;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// Deletes items (spec §12.5). On a local drive "delete" is the Windows Recycle Bin; on a network share it is a soft delete, a rename
/// into the share's own recycle folder (Synology <c>#recycle</c>, QNAP <c>@Recycle</c>) or, where there is none, into a hidden
/// <c>.leviathan-trash</c> at the share root, so it is instant even for a huge file and Restore has the original path. Permanent
/// delete only runs when the request says so, which the UI asks only after a confirmation.
/// </summary>
public sealed class DeleteExecutor : IJobExecutor
{
    /// <summary>The hidden folder at the root of a share where Leviathan keeps what was soft-deleted and its manifest.</summary>
    public const string TrashFolder = RecycleFolders.TrashFolder;

    /// <summary>The manifest of soft deletes in <see cref="TrashFolder"/>: one JSON line per item with its original path.</summary>
    public const string ManifestFile = "manifest.jsonl";

    private readonly IFileSystem _fs;
    private readonly IRecycleBin _bin;
    private readonly TimeProvider _time;
    private readonly RecycleFolders _folders;

    /// <summary>Creates the executor.</summary>
    /// <param name="fileSystem">The disk.</param>
    /// <param name="recycleBin">The Windows Recycle Bin for local drives.</param>
    /// <param name="time">Clock.</param>
    /// <param name="recycleFolders">Finds (and remembers) each share's recycle folder; a new one when null.</param>
    public DeleteExecutor(IFileSystem fileSystem, IRecycleBin recycleBin, TimeProvider? time = null, RecycleFolders? recycleFolders = null)
    {
        _folders = recycleFolders ?? new RecycleFolders(fileSystem);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(recycleBin);
        _fs = fileSystem;
        _bin = recycleBin;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.Delete;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var sources = request.Sources.Select(LocationPath.Parse).ToList();
        var failures = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[i];
            context.Report(0, -1, i, sources.Count, source.Name);
            ItemOutcome outcome;
            try
            {
                outcome = await DeleteOneAsync(source, request.DeleteMode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome = new ItemOutcome(source.Value, null, ItemStatus.Failed, ex.Message);
            }

            context.AddResult(outcome);
            if (outcome.Status == ItemStatus.Failed)
            {
                failures.Add($"{source.Name}: {outcome.Error}");
            }
        }

        context.Report(0, -1, sources.Count, sources.Count);
        return failures.Count == 0
            ? JobOutcome.Completed
            : new JobOutcome(JobState.Failed, $"{failures.Count} of {sources.Count} item(s) could not be deleted. {string.Join(" ", failures.Take(3))}");
    }

    private static string Io(LocationPath path) => path.ToWin32Path();

    private async Task<ItemOutcome> DeleteOneAsync(LocationPath source, DeleteMode mode, CancellationToken ct)
    {
        var isFolder = _fs.Directory.Exists(Io(source));
        if (!isFolder && !_fs.File.Exists(Io(source)))
        {
            // The folder view can show an item that was removed a moment ago (a cached listing, another program): the goal of the delete
            // is reached, so it is not a failure and does not turn the job red. It is listed in the report as left alone.
            return new ItemOutcome(source.Value, null, ItemStatus.Skipped, $"{source.Name} was already gone.");
        }

        if (source.IsRoot)
        {
            return new ItemOutcome(source.Value, null, ItemStatus.Failed, "A drive or share cannot be deleted.");
        }

        if (mode == DeleteMode.Permanent)
        {
            DeletePermanently(source, isFolder);
            return new ItemOutcome(source.Value, null, ItemStatus.Done);
        }

        if (source.Kind == LocationKind.Unc)
        {
            if (RecycleFolders.IsInsideBin(source))
            {
                return new ItemOutcome(source.Value, null, ItemStatus.Failed, $"{source.Name} is already in a recycle folder. Use Shift+Delete to delete it permanently.");
            }

            var trashed = SoftDelete(source);
            return new ItemOutcome(source.Value, trashed.Value, ItemStatus.Done);
        }

        // Windows gives no way to ask where the item went, so look it up; without it the delete just cannot be undone (spec §12.5).
        var started = _time.GetUtcNow() - TimeSpan.FromSeconds(5);
        await _bin.RecycleAsync(source, ct).ConfigureAwait(false);
        var recycled = RecycleBinIndex.FindRecycled(_fs, source, started);
        return new ItemOutcome(source.Value, recycled?.Value, ItemStatus.Done);
    }

    private void DeletePermanently(LocationPath source, bool isFolder)
    {
        if (!isFolder)
        {
            ClearReadOnly(Io(source));
            _fs.File.Delete(Io(source));
            return;
        }

        // A junction or symbolic link is removed itself; what it points at is never touched.
        if (_fs.File.GetAttributes(Io(source)).HasFlag(FileAttributes.ReparsePoint))
        {
            _fs.Directory.Delete(Io(source), recursive: false);
            return;
        }

        foreach (var file in _fs.Directory.EnumerateFiles(Io(source), "*", SearchOption.AllDirectories))
        {
            ClearReadOnly(file);
        }

        _fs.Directory.Delete(Io(source), recursive: true);
    }

    private void ClearReadOnly(string path)
    {
        var attributes = _fs.File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            _fs.File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>Moves <paramref name="source"/> into the share's recycle folder and records where it came from. Returns where it is now.</summary>
    private LocationPath SoftDelete(LocationPath source)
    {
        var now = _time.GetUtcNow();
        var recycle = _folders.Resolve(source);
        var trashRoot = recycle.TrashRoot;

        LocationPath target;
        if (recycle.Kind != RecycleKind.Leviathan)
        {
            // The share's own bin keeps the folder structure, as its web UI expects.
            var folder = recycle.Folder;
            foreach (var segment in source.Segments.Take(source.Segments.Count - 1))
            {
                folder = folder.Combine(segment);
            }

            _fs.Directory.CreateDirectory(Io(folder));
            target = Unique(folder, source.Name);
        }
        else
        {
            var batch = trashRoot.Combine($"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
            _fs.Directory.CreateDirectory(Io(batch));
            target = batch.Combine(source.Name);
        }

        if (_fs.Directory.Exists(Io(source)))
        {
            _fs.Directory.Move(Io(source), Io(target));
        }
        else
        {
            _fs.File.Move(Io(source), Io(target));
        }

        var line = JsonSerializer.Serialize(new { original = source.Value, trashed = target.Value, at = now }) + "\n";
        _fs.File.AppendAllText(Io(trashRoot.Combine(ManifestFile)), line);
        return target;
    }

    private LocationPath Unique(LocationPath folder, string name)
    {
        var candidate = folder.Combine(name);
        var stem = _fs.Path.GetFileNameWithoutExtension(name);
        var extension = _fs.Path.GetExtension(name);
        for (var n = 2; _fs.File.Exists(Io(candidate)) || _fs.Directory.Exists(Io(candidate)); n++)
        {
            candidate = folder.Combine($"{stem} ({n}){extension}");
        }

        return candidate;
    }
}
