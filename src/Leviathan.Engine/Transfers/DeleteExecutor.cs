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
    public const string TrashFolder = ".leviathan-trash";

    /// <summary>The manifest of soft deletes in <see cref="TrashFolder"/>: one JSON line per item with its original path.</summary>
    public const string ManifestFile = "manifest.jsonl";

    private static readonly string[] ShareRecycleFolders = ["#recycle", "@Recycle"];

    private readonly IFileSystem _fs;
    private readonly IRecycleBin _bin;
    private readonly TimeProvider _time;

    /// <summary>Creates the executor.</summary>
    public DeleteExecutor(IFileSystem fileSystem, IRecycleBin recycleBin, TimeProvider? time = null)
    {
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
            return new ItemOutcome(source.Value, null, ItemStatus.Failed, $"{source.Name} no longer exists.");
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
            var trashed = SoftDelete(source);
            return new ItemOutcome(source.Value, trashed.Value, ItemStatus.Done);
        }

        await _bin.RecycleAsync(source, ct).ConfigureAwait(false);
        return new ItemOutcome(source.Value, null, ItemStatus.Done);
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
        var shareRoot = LocationPath.Parse(source.Root);
        var now = _time.GetUtcNow();
        var shareBin = ShareRecycleFolders.Select(shareRoot.Combine).FirstOrDefault(p => _fs.Directory.Exists(Io(p)));
        var trashRoot = shareRoot.Combine(TrashFolder);
        EnsureTrashFolder(trashRoot);

        LocationPath target;
        if (shareBin is not null)
        {
            // The share's own bin keeps the folder structure, as its web UI expects.
            var folder = shareBin;
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

    private void EnsureTrashFolder(LocationPath trashRoot)
    {
        if (_fs.Directory.Exists(Io(trashRoot)))
        {
            return;
        }

        _fs.Directory.CreateDirectory(Io(trashRoot));

        // Hidden, so other tools and Leviathan's own listings do not show it by default (spec §12.5).
        _fs.DirectoryInfo.New(Io(trashRoot)).Attributes = FileAttributes.Directory | FileAttributes.Hidden;
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
