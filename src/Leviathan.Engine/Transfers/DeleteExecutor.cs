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
    private readonly IVolumeInfo? _volumes;

    /// <summary>Creates the executor.</summary>
    /// <param name="fileSystem">The disk.</param>
    /// <param name="recycleBin">The Windows Recycle Bin for local drives.</param>
    /// <param name="time">Clock.</param>
    /// <param name="recycleFolders">Finds (and remembers) each share's recycle folder; a new one when null.</param>
    /// <param name="volumes">Tells what kind of drive a drive letter is, so plain Delete follows the table of spec §12.5; when null every drive letter is trusted to have a Recycle Bin.</param>
    public DeleteExecutor(IFileSystem fileSystem, IRecycleBin recycleBin, TimeProvider? time = null, RecycleFolders? recycleFolders = null, IVolumeInfo? volumes = null)
    {
        _volumes = volumes;
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
                outcome = await DeleteOneAsync(source, request.DeleteMode, request.KeepOwnTrash, cancellationToken).ConfigureAwait(false);
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

    private async Task<ItemOutcome> DeleteOneAsync(LocationPath source, DeleteMode mode, bool keepOwnTrash, CancellationToken ct)
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

        // Plain Delete follows the table of spec §12.5. The Windows Recycle Bin call is silent, and on a drive with no bin it deletes for
        // good, so a mapped drive is soft-deleted through its share and a drive without a bin is refused: the window asks the user first
        // and then sends a permanent delete.
        var target = source;
        if (source.Kind == LocationKind.Drive && _volumes is not null)
        {
            var drive = await _volumes.GetDriveAsync(source, ct).ConfigureAwait(false);
            switch (DeletePolicy.Decide(source, drive))
            {
                case DeleteBehavior.PermanentAfterConfirmation:
                    return new ItemOutcome(source.Value, null, ItemStatus.Failed, $"{source.Name} is on a drive with no Recycle Bin. Use Shift+Delete to delete it permanently.");
                case DeleteBehavior.SoftDelete:
                    if (!TryShareEquivalent(source, drive, out target))
                    {
                        return new ItemOutcome(source.Value, null, ItemStatus.Failed, $"Leviathan cannot tell which share {source.Root} points at, so {source.Name} was left alone. Use Shift+Delete to delete it permanently.");
                    }

                    break;
            }
        }

        if (target.Kind == LocationKind.Unc)
        {
            if (RecycleFolders.IsInsideBin(target))
            {
                return new ItemOutcome(source.Value, null, ItemStatus.Failed, $"{source.Name} is already in a recycle folder. Use Shift+Delete to delete it permanently.");
            }

            var trashed = SoftDelete(target, keepOwnTrash);
            if (trashed is null)
            {
                // Leviathan's own folder is off and no NAS bin is visible: delete on the share and let the NAS's own recycling (if on)
                // keep it. There is nothing here to undo, which the empty result says.
                DeletePermanently(source, isFolder);
                return new ItemOutcome(source.Value, null, ItemStatus.Done);
            }

            return new ItemOutcome(source.Value, trashed.Value, ItemStatus.Done);
        }

        // Windows gives no way to ask where the item went, so look it up; without it the delete just cannot be undone (spec §12.5).
        var started = _time.GetUtcNow() - TimeSpan.FromSeconds(5);
        await _bin.RecycleAsync(source, ct).ConfigureAwait(false);
        var recycled = RecycleBinIndex.FindRecycled(_fs, source, started);
        return new ItemOutcome(source.Value, recycled?.Value, ItemStatus.Done);
    }

    // Y:\show\ep1.mkv on a drive mapped to \\nas\media becomes \\nas\media\show\ep1.mkv, where the share's recycle folder lives.
    private static bool TryShareEquivalent(LocationPath source, DriveDetails? drive, out LocationPath share)
    {
        share = source;
        if (drive?.NetworkPath is not { Length: > 0 } network || !LocationPath.TryParse(network, out var root) || root.Kind != LocationKind.Unc)
        {
            return false;
        }

        foreach (var segment in source.Segments)
        {
            root = root.Combine(segment);
        }

        share = root;
        return true;
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

    /// <summary>
    /// Moves <paramref name="source"/> into the share's recycle folder and records where it came from. Returns where it is now, or null
    /// when Leviathan's own folder is off and the share has no bin to use (the caller then deletes it on the share).
    /// </summary>
    private LocationPath? SoftDelete(LocationPath source, bool keepOwnTrash)
    {
        var now = _time.GetUtcNow();
        var recycle = keepOwnTrash ? _folders.Resolve(source) : _folders.FindShareBin(source);
        if (recycle is null)
        {
            return null;
        }

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
            var batch = trashRoot!.Combine($"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
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

        if (trashRoot is not null)
        {
            var line = JsonSerializer.Serialize(new { original = source.Value, trashed = target.Value, at = now }) + "\n";
            _fs.File.AppendAllText(Io(trashRoot.Combine(ManifestFile)), line);
        }

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
