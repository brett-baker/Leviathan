using System.IO.Abstractions;
using System.Security.Cryptography;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// Copies and moves files and folders on local disks and Windows shares (spec §12.1). Data goes to <c>name.leviathan-part</c> and is
/// renamed into place only when complete, so a half-copied file never looks finished. A move on one volume is a rename; a move
/// across volumes copies, checks the copy against the source by hash, and only then deletes the source. Name clashes follow the
/// job's <see cref="ConflictPolicy"/>. This is the plain chunked copier of Phase 1: it does not resume a file after a crash
/// (Phase 2 adds the per-file journal), but it never leaves a finished-looking partial file and never deletes an unverified source.
/// </summary>
public sealed class CopyMoveExecutor : IJobExecutor
{
    /// <summary>Suffix of a file still being written.</summary>
    public const string PartSuffix = ".leviathan-part";

    private readonly IFileSystem _fs;
    private readonly int _bufferSize;

    /// <summary>Creates the executor over <paramref name="fileSystem"/> with <paramref name="bufferSize"/>-byte chunks (1 MB by default).</summary>
    public CopyMoveExecutor(IFileSystem fileSystem, int bufferSize = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _fs = fileSystem;
        _bufferSize = bufferSize;
    }

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind is JobKind.Copy or JobKind.Move;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var destination = LocationPath.Parse(request.Destination!);
        var move = request.Kind == JobKind.Move;
        var sources = request.Sources.Select(LocationPath.Parse).ToList();

        var run = new Run(context, request.Conflict, move);
        run.Plan(sources, this);
        if (!_fs.Directory.Exists(Io(destination)))
        {
            return new JobOutcome(JobState.Failed, $"{destination.Name} does not exist.");
        }

        if (run.NeedsSpaceFor(sources, destination) is { } lack && FreeSpace(destination) is { } free && free < lack)
        {
            return new JobOutcome(JobState.Failed, $"Not enough space on {destination.Root}: needs {lack:N0} bytes, {free:N0} free.");
        }

        var failures = new List<string>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ItemOutcome outcome;
            try
            {
                outcome = await TransferTopLevelAsync(run, source, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome = new ItemOutcome(source.Value, null, ItemStatus.Failed, ex.Message);
            }

            context.AddResult(outcome);
            run.ItemsDone++;
            run.Publish(null);
            if (outcome.Status == ItemStatus.Failed)
            {
                failures.Add($"{source.Name}: {outcome.Error}");
            }
        }

        return failures.Count == 0
            ? JobOutcome.Completed
            : new JobOutcome(JobState.Failed, $"{failures.Count} of {sources.Count} item(s) failed. {string.Join(" ", failures.Take(3))}");
    }

    private static bool SameVolume(LocationPath a, LocationPath b) =>
        a.Kind == b.Kind && string.Equals(a.Root, b.Root, StringComparison.OrdinalIgnoreCase);

    private static string Io(LocationPath path) => path.ToWin32Path();

    private long? FreeSpace(LocationPath destination)
    {
        try
        {
            return _fs.DriveInfo.New(destination.Root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            // Shares and odd roots may not answer; the copy itself will report a full disk.
            return null;
        }
    }

    private async Task<ItemOutcome> TransferTopLevelAsync(Run run, LocationPath source, LocationPath destinationFolder, CancellationToken ct)
    {
        var isFolder = _fs.Directory.Exists(Io(source));
        if (!isFolder && !_fs.File.Exists(Io(source)))
        {
            return new ItemOutcome(source.Value, null, ItemStatus.Failed, $"{source.Name} no longer exists.");
        }

        var target = destinationFolder.Combine(source.Name);
        var exists = _fs.File.Exists(Io(target)) || _fs.Directory.Exists(Io(target));
        var samePlace = source.Equals(target);
        if (samePlace && run.Move)
        {
            return new ItemOutcome(source.Value, source.Value, ItemStatus.Skipped, "Already in this folder.");
        }

        if (exists && !samePlace && run.Conflict == ConflictPolicy.Skip)
        {
            return new ItemOutcome(source.Value, null, ItemStatus.Skipped, "Something with that name is already there.");
        }

        // Replace merges a folder into a folder (files of the same name are overwritten). A file meeting a folder, or the other way
        // round, cannot be replaced safely, so it keeps both. Copying into its own folder always makes a new name.
        var kindMismatch = exists && isFolder != _fs.Directory.Exists(Io(target));
        if (samePlace || (exists && (run.Conflict == ConflictPolicy.KeepBoth || kindMismatch)))
        {
            target = UniqueName(destinationFolder, source.Name, isFolder);
            exists = false;
        }

        var sameVolume = SameVolume(source, target);
        if (run.Move && sameVolume && !exists)
        {
            // Same volume: a rename, instant, nothing copied.
            if (isFolder)
            {
                _fs.Directory.Move(Io(source), Io(target));
            }
            else
            {
                _fs.File.Move(Io(source), Io(target));
            }

            run.AddBytes(run.SizeOf(source));
            return new ItemOutcome(source.Value, target.Value, ItemStatus.Done);
        }

        if (isFolder)
        {
            await TransferFolderAsync(run, source, target, sameVolume, ct).ConfigureAwait(false);
        }
        else
        {
            await TransferFileAsync(run, source, target, sameVolume, ct).ConfigureAwait(false);
        }

        return new ItemOutcome(source.Value, target.Value, ItemStatus.Done);
    }

    private async Task TransferFolderAsync(Run run, LocationPath source, LocationPath target, bool sameVolume, CancellationToken ct)
    {
        _fs.Directory.CreateDirectory(Io(target));
        foreach (var entry in _fs.Directory.EnumerateFileSystemEntries(Io(source)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var name = _fs.Path.GetFileName(entry);
            var child = source.Combine(name);
            var childTarget = target.Combine(name);
            if (_fs.Directory.Exists(entry))
            {
                await TransferFolderAsync(run, child, childTarget, sameVolume, ct).ConfigureAwait(false);
            }
            else
            {
                if (_fs.File.Exists(Io(childTarget)))
                {
                    if (run.Conflict == ConflictPolicy.Skip)
                    {
                        run.AddBytes(_fs.FileInfo.New(entry).Length);
                        continue;
                    }

                    if (run.Conflict == ConflictPolicy.KeepBoth)
                    {
                        childTarget = UniqueName(target, name, isFolder: false);
                    }
                }

                await TransferFileAsync(run, child, childTarget, sameVolume, ct).ConfigureAwait(false);
            }
        }

        if (run.Move && IsEmpty(source))
        {
            _fs.Directory.Delete(Io(source));
        }
    }

    private bool IsEmpty(LocationPath folder) => !_fs.Directory.EnumerateFileSystemEntries(Io(folder)).Any();

    private async Task TransferFileAsync(Run run, LocationPath source, LocationPath target, bool sameVolume, CancellationToken ct)
    {
        if (run.Move && sameVolume)
        {
            _fs.File.Move(Io(source), Io(target), overwrite: true);
            run.AddBytes(_fs.FileInfo.New(Io(target)).Length);
            return;
        }

        var part = Io(target) + PartSuffix;
        var info = _fs.FileInfo.New(Io(source));
        var length = info.Length;
        run.Publish(source.Name);
        byte[]? sourceHash = null;
        try
        {
            using (var input = _fs.File.Open(Io(source), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = _fs.File.Open(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[_bufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    run.AddBytes(read);
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
                sourceHash = hash.GetHashAndReset();
            }

            if (_fs.FileInfo.New(part).Length != length)
            {
                throw new IOException($"The copy of {source.Name} is {_fs.FileInfo.New(part).Length:N0} bytes, not {length:N0}.");
            }

            _fs.File.SetLastWriteTimeUtc(part, info.LastWriteTimeUtc);
            _fs.File.SetCreationTimeUtc(part, info.CreationTimeUtc);
            _fs.File.SetAttributes(part, info.Attributes & ~FileAttributes.ReadOnly);
            _fs.File.Move(part, Io(target), overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        if (run.Move)
        {
            // Across volumes the original goes only after the copy is read back and matches (spec §12.1).
            if (!await MatchesAsync(Io(target), sourceHash!, ct).ConfigureAwait(false))
            {
                TryDelete(Io(target));
                throw new IOException($"The copy of {source.Name} did not match the original, so the original was kept.");
            }

            _fs.File.Delete(Io(source));
        }
    }

    private async Task<bool> MatchesAsync(string path, byte[] expected, CancellationToken ct)
    {
        using var stream = _fs.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[_bufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset().AsSpan().SequenceEqual(expected);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_fs.File.Exists(path))
            {
                _fs.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .leviathan-part is harmless: it is never mistaken for a finished file.
        }
    }

    /// <summary>"name (2).ext", "name (3).ext", … the first one that is free in <paramref name="folder"/>.</summary>
    private LocationPath UniqueName(LocationPath folder, string name, bool isFolder)
    {
        var stem = isFolder ? name : _fs.Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? string.Empty : _fs.Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = folder.Combine($"{stem} ({n}){extension}");
            if (!_fs.File.Exists(Io(candidate)) && !_fs.Directory.Exists(Io(candidate)))
            {
                return candidate;
            }
        }
    }

    /// <summary>Progress bookkeeping for one job.</summary>
    private sealed class Run(JobContext context, ConflictPolicy conflict, bool move)
    {
        private long _bytesDone;
        private long _bytesTotal = -1;
        private int _itemsTotal = -1;
        private readonly Dictionary<LocationPath, long> _sizes = [];
        private string? _current;

        public ConflictPolicy Conflict { get; } = conflict;

        public bool Move { get; } = move;

        public int ItemsDone { get; set; }

        public long SizeOf(LocationPath source) => _sizes.TryGetValue(source, out var size) ? size : 0;

        public void Plan(List<LocationPath> sources, CopyMoveExecutor owner)
        {
            long total = 0;
            foreach (var source in sources)
            {
                var size = owner.MeasureQuietly(source);
                _sizes[source] = size;
                total += size;
            }

            _bytesTotal = total;
            _itemsTotal = sources.Count;
            Publish(null);
        }

        /// <summary>Bytes to write at the destination, or null for a same-volume move, which writes nothing.</summary>
        public long? NeedsSpaceFor(List<LocationPath> sources, LocationPath destination) =>
            Move && sources.All(s => SameVolume(s, destination)) ? null : _bytesTotal;

        public void AddBytes(long bytes)
        {
            _bytesDone += bytes;
            Publish(_current);
        }

        public void Publish(string? current)
        {
            _current = current ?? _current;
            context.Report(_bytesDone, _bytesTotal, ItemsDone, _itemsTotal, _current);
        }
    }

    private long MeasureQuietly(LocationPath source)
    {
        try
        {
            if (_fs.File.Exists(Io(source)))
            {
                return _fs.FileInfo.New(Io(source)).Length;
            }

            return _fs.Directory.Exists(Io(source))
                ? _fs.Directory.EnumerateFiles(Io(source), "*", SearchOption.AllDirectories).Sum(f => _fs.FileInfo.New(f).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable parts are reported when the copy reaches them; the total is only for the progress bar.
            return 0;
        }
    }
}
