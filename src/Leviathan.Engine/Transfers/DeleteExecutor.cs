using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Text.Json;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// Deletes items (spec §12.5). On a local drive "delete" is the Windows Recycle Bin; on a network share it is a soft delete, a rename
/// into the share's own recycle folder (Synology <c>#recycle</c>, QNAP <c>@Recycle</c>) or, where there is none, into a hidden
/// <c>.leviathan-trash</c> at the share root, so it is instant even for a huge file and Restore has the original path. Permanent
/// delete only runs when the request says so, which the UI asks only after a confirmation.
/// </summary>
/// <remarks>
/// A delete of tens of thousands of items on a share is bound by round trips, not by bytes (spec §3.3: the network is the slow part).
/// So one job looks things up once (the drive, the share's recycle folder, the trash batch folder), needs one call to learn whether an
/// item is a file, a folder or gone, writes the manifest in batches, and works on several items at once. The Recycle Bin of Windows
/// is an STA shell call and stays one at a time.
/// </remarks>
public sealed class DeleteExecutor : IJobExecutor
{
    /// <summary>The hidden folder at the root of a share where Leviathan keeps what was soft-deleted and its manifest.</summary>
    public const string TrashFolder = RecycleFolders.TrashFolder;

    /// <summary>The manifest of soft deletes in <see cref="TrashFolder"/>: one JSON line per item with its original path.</summary>
    public const string ManifestFile = "manifest.jsonl";

    /// <summary>How many items of one job are deleted at the same time unless the caller says otherwise.</summary>
    public const int DefaultParallelism = 8;

    // A crash loses at most this many manifest lines; the items stay in their trash batch folder and nothing is deleted for good.
    private const int ManifestBatch = 50;

    private readonly IFileSystem _fs;
    private readonly IRecycleBin _bin;
    private readonly TimeProvider _time;
    private readonly RecycleFolders _folders;
    private readonly IVolumeInfo? _volumes;
    private readonly BinRegistry? _binRegistry;
    private readonly int _maxParallel;

    /// <summary>Creates the executor.</summary>
    /// <param name="fileSystem">The disk.</param>
    /// <param name="recycleBin">The Windows Recycle Bin for local drives.</param>
    /// <param name="time">Clock.</param>
    /// <param name="recycleFolders">Finds (and remembers) each share's recycle folder; a new one when null.</param>
    /// <param name="volumes">Tells what kind of drive a drive letter is, so plain Delete follows the table of spec §12.5; when null every drive letter is trusted to have a Recycle Bin.</param>
    /// <param name="bin">Remembers each trash folder used, so the Soft-delete bin can find the items (spec §12.5); not kept when null.</param>
    /// <param name="maxParallel">How many items of one job are deleted at once (1 = one after the other).</param>
    public DeleteExecutor(IFileSystem fileSystem, IRecycleBin recycleBin, TimeProvider? time = null, RecycleFolders? recycleFolders = null, IVolumeInfo? volumes = null, BinRegistry? bin = null, int maxParallel = DefaultParallelism)
    {
        _volumes = volumes;
        _binRegistry = bin;
        _folders = recycleFolders ?? new RecycleFolders(fileSystem);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(recycleBin);
        _fs = fileSystem;
        _bin = recycleBin;
        _time = time ?? TimeProvider.System;
        _maxParallel = Math.Max(1, maxParallel);
    }

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.Delete;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var sources = request.Sources.Select(LocationPath.ParseExact).ToList();
        var outcomes = new ItemOutcome?[sources.Count];
        var run = new Run();
        var done = 0;
        var gate = new object();
        LocationException? dropped = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        context.Report(0, -1, 0, sources.Count);
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = _maxParallel, CancellationToken = stop.Token };
            await Parallel.ForEachAsync(Enumerable.Range(0, sources.Count), options, async (i, token) =>
            {
                var source = sources[i];
                ItemOutcome outcome;
                try
                {
                    outcome = await DeleteOneAsync(run, source, request.DeleteMode, request.KeepOwnTrash, token).ConfigureAwait(false);
                }
                catch (IOException ex) when (LocationErrors.Wrap(ex, source) is { IsTransient: true } transient)
                {
                    // The share dropped (spec 3.3): stop starting items; the host waits and runs the job again. Items already gone then
                    // count as "already gone", so a repeat is safe.
                    lock (gate)
                    {
                        dropped ??= transient;
                    }

                    await stop.CancelAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Gone between the look and the delete (another item of this job held it, or another program): the goal is reached.
                    outcome = new ItemOutcome(source.Value, null, ItemStatus.Skipped, $"{source.Name} was already gone.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    outcome = new ItemOutcome(source.Value, null, ItemStatus.Failed, ex.Message);
                }

                outcomes[i] = outcome;
                context.Report(0, -1, Interlocked.Increment(ref done), sources.Count, source.Name);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (dropped is not null && !cancellationToken.IsCancellationRequested)
        {
            // Stopped by a dropped share: reported below, after what was done is written down.
        }
        finally
        {
            // What was done is recorded whatever stopped the job (a drop, a cancel), in the order of the request, so undo and the report
            // can rely on it; the manifest lines of items already moved are written for the same reason.
            run.FlushManifests(_fs);
            foreach (var outcome in outcomes)
            {
                if (outcome is not null)
                {
                    context.AddResult(outcome);
                }
            }
        }

        if (dropped is not null)
        {
            throw dropped;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<string>();
        for (var i = 0; i < outcomes.Length; i++)
        {
            if (outcomes[i] is { Status: ItemStatus.Failed } failed)
            {
                failures.Add($"{sources[i].Name}: {failed.Error}");
            }
        }

        context.Report(0, -1, sources.Count, sources.Count);
        return failures.Count == 0
            ? JobOutcome.Completed
            : new JobOutcome(JobState.Failed, $"{failures.Count} of {sources.Count} item(s) could not be deleted. {string.Join(" ", failures.Take(3))}");
    }

    private static string Io(LocationPath path) => path.ToWin32Path();

    private async Task<ItemOutcome> DeleteOneAsync(Run run, LocationPath source, DeleteMode mode, bool keepOwnTrash, CancellationToken ct)
    {
        // One call tells whether it is a folder, a file or not there: each call is a round trip on a share.
        FileAttributes attributes;
        try
        {
            attributes = _fs.File.GetAttributes(Io(source));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The folder view can show an item that was removed a moment ago (a cached listing, another program): the goal of the delete
            // is reached, so it is not a failure and does not turn the job red. It is listed in the report as left alone.
            return new ItemOutcome(source.Value, null, ItemStatus.Skipped, $"{source.Name} was already gone.");
        }

        var isFolder = attributes.HasFlag(FileAttributes.Directory);
        if (source.IsRoot)
        {
            return new ItemOutcome(source.Value, null, ItemStatus.Failed, "A drive or share cannot be deleted.");
        }

        if (mode == DeleteMode.Permanent)
        {
            PermanentDelete.Run(_fs, Io(source), isFolder);
            return new ItemOutcome(source.Value, null, ItemStatus.Done);
        }

        // Plain Delete follows the table of spec §12.5. The Windows Recycle Bin call is silent, and on a drive with no bin it deletes for
        // good, so a mapped drive is soft-deleted through its share and a drive without a bin is refused: the window asks the user first
        // and then sends a permanent delete.
        var target = source;
        if (source.Kind == LocationKind.Drive && _volumes is not null)
        {
            var drive = await run.DriveAsync(source, _volumes, ct).ConfigureAwait(false);
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

            var trashed = SoftDelete(run, target, isFolder, keepOwnTrash);
            if (trashed is null)
            {
                // Leviathan's own folder is off and no NAS bin is visible: delete on the share and let the NAS's own recycling (if on)
                // keep it. There is nothing here to undo, which the empty result says.
                PermanentDelete.Run(_fs, Io(source), isFolder);
                return new ItemOutcome(source.Value, null, ItemStatus.Done);
            }

            return new ItemOutcome(source.Value, trashed.Value, ItemStatus.Done);
        }

        // Windows gives no way to ask where the item went, so look it up; without it the delete just cannot be undone (spec §12.5).
        // The shell call is an STA call: one at a time.
        await run.BinGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var started = _time.GetUtcNow() - TimeSpan.FromSeconds(5);
            await _bin.RecycleAsync(source, ct).ConfigureAwait(false);
            var recycled = RecycleBinIndex.FindRecycled(_fs, source, started);
            return new ItemOutcome(source.Value, recycled?.Value, ItemStatus.Done);
        }
        finally
        {
            run.BinGate.Release();
        }
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

    /// <summary>
    /// Moves <paramref name="source"/> into the share's recycle folder and records where it came from. Returns where it is now, or null
    /// when Leviathan's own folder is off and the share has no bin to use (the caller then deletes it on the share).
    /// </summary>
    private LocationPath? SoftDelete(Run run, LocationPath source, bool isFolder, bool keepOwnTrash)
    {
        var recycle = run.Recycle(source.Root, keepOwnTrash, () => keepOwnTrash ? _folders.Resolve(source) : _folders.FindShareBin(source));
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

            run.EnsureFolder(_fs, folder);
            target = Unique(folder, source.Name);
        }
        else
        {
            // One batch folder for the whole job and share, not one per item.
            var batch = run.Batch(trashRoot!, () => trashRoot!.Combine($"{_time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"), _fs);
            target = batch.Combine(source.Name);
        }

        if (isFolder)
        {
            _fs.Directory.Move(Io(source), Io(target));
        }
        else
        {
            _fs.File.Move(Io(source), Io(target));
        }

        if (trashRoot is not null)
        {
            var line = JsonSerializer.Serialize(new { original = source.Value, trashed = target.Value, at = _time.GetUtcNow() }) + "\n";
            run.AddManifestLine(_fs, trashRoot, line, ManifestBatch);
            if (run.FirstUse(trashRoot))
            {
                _binRegistry?.Add(trashRoot);
            }
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

    /// <summary>What one job looks up once and shares between its items.</summary>
    private sealed class Run
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<DriveDetails?>>> _drives = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<RecycleFolder?>> _recycle = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<LocationPath>> _batches = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _used = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (LocationPath Root, List<string> Lines)> _manifest = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _manifestLock = new();

        /// <summary>Serializes the Windows Recycle Bin calls (an STA shell call).</summary>
        public SemaphoreSlim BinGate { get; } = new(1, 1);

        public Task<DriveDetails?> DriveAsync(LocationPath source, IVolumeInfo volumes, CancellationToken ct) =>
            _drives.GetOrAdd(source.Root, _ => new Lazy<Task<DriveDetails?>>(() => volumes.GetDriveAsync(source, ct))).Value;

        public RecycleFolder? Recycle(string shareRoot, bool keepOwnTrash, Func<RecycleFolder?> resolve) =>
            _recycle.GetOrAdd($"{shareRoot}|{keepOwnTrash}", _ => new Lazy<RecycleFolder?>(resolve)).Value;

        public LocationPath Batch(LocationPath trashRoot, Func<LocationPath> name, IFileSystem fs) =>
            _batches.GetOrAdd(trashRoot.Value, _ => new Lazy<LocationPath>(() =>
            {
                var batch = name();
                fs.Directory.CreateDirectory(batch.ToWin32Path());
                return batch;
            })).Value;

        public void EnsureFolder(IFileSystem fs, LocationPath folder)
        {
            if (_folders.TryAdd(folder.Value, 0))
            {
                fs.Directory.CreateDirectory(folder.ToWin32Path());
            }
        }

        public bool FirstUse(LocationPath trashRoot) => _used.TryAdd(trashRoot.Value, 0);

        public void AddManifestLine(IFileSystem fs, LocationPath trashRoot, string line, int batch)
        {
            List<string>? ready = null;
            lock (_manifestLock)
            {
                if (!_manifest.TryGetValue(trashRoot.Value, out var entry))
                {
                    entry = (trashRoot, []);
                    _manifest[trashRoot.Value] = entry;
                }

                entry.Lines.Add(line);
                if (entry.Lines.Count >= batch)
                {
                    ready = [.. entry.Lines];
                    entry.Lines.Clear();
                }
            }

            if (ready is not null)
            {
                Append(fs, trashRoot, ready);
            }
        }

        public void FlushManifests(IFileSystem fs)
        {
            List<(LocationPath Root, List<string> Lines)> pending;
            lock (_manifestLock)
            {
                pending = [.. _manifest.Values.Where(e => e.Lines.Count > 0).Select(e => (e.Root, new List<string>(e.Lines)))];
                foreach (var entry in _manifest.Values)
                {
                    entry.Lines.Clear();
                }
            }

            foreach (var (root, lines) in pending)
            {
                Append(fs, root, lines);
            }
        }

        // One write for many lines; two writers never interleave because each takes the same lock.
        private void Append(IFileSystem fs, LocationPath trashRoot, List<string> lines)
        {
            lock (_manifestLock)
            {
                fs.File.AppendAllText(trashRoot.Combine(ManifestFile).ToWin32Path(), string.Concat(lines));
            }
        }
    }
}
