using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Archives;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Providers.Archive;

/// <summary>
/// Runs <see cref="JobKind.Extract"/> (spec §15.4, §12.3): unpacks archives into a folder, in one pass over each archive, with
/// progress in bytes and files. Every file is written to a <c>.leviathan-part</c> file and renamed when it is complete (spec §3.4), so a
/// stopped or crashed job leaves no half-written file under a real name. Names that would land outside the folder or that Windows cannot
/// use are refused file by file, not for the whole archive. Name clashes follow the job's <see cref="JobRequest.Conflict"/> rule
/// (keep both, replace or skip); the job never overwrites unless told to.
/// </summary>
public sealed class ArchiveExtractExecutor(IArchiveReader reader, IFileSystem fileSystem) : IJobExecutor
{
    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.Extract;

    private IFileSystem Files => fileSystem;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var destination = LocationPath.Parse(request.Destination!);
        if (destination.Kind is not (LocationKind.Drive or LocationKind.Unc))
        {
            return new JobOutcome(JobState.Failed, "Archives can only be extracted to a folder on this PC or on a Windows share.");
        }

        var baseFolder = destination.ToWin32Path();
        var archives = new List<(string Source, string Path, IReadOnlyList<ArchiveEntryInfo> Entries)>();
        foreach (var source in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = LocationPath.Parse(source);
            if (location.Kind is not (LocationKind.Drive or LocationKind.Unc) || ArchiveKinds.IsInside(location))
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Skipped, "Only archive files on this PC or on a Windows share can be extracted."));
                continue;
            }

            var path = location.ToWin32Path();
            if (!fileSystem.File.Exists(path))
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Failed, "The archive is not there."));
                continue;
            }

            try
            {
                archives.Add((source, path, await reader.ReadEntriesAsync(path, request.ArchivePassword, cancellationToken).ConfigureAwait(false)));
            }
            catch (ArchiveException ex)
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Failed, ex.Message));
            }
        }

        var files = archives.Sum(a => a.Entries.Count(e => !e.IsDirectory));
        var bytes = archives.Sum(a => a.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size));
        var progress = new Progress(context, bytes, files);
        context.Report(0, bytes, 0, files);

        var extracted = 0;
        foreach (var (source, path, entries) in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = baseFolder;
            if (request.ExtractIntoSubfolder)
            {
                root = fileSystem.Path.Combine(baseFolder, ExtractPaths.FolderNameFor(fileSystem.Path.GetFileName(path)));
                if (request.Conflict == ConflictPolicy.KeepBoth && fileSystem.Directory.Exists(root))
                {
                    root = FreeName(root, isFile: false);
                }
            }

            fileSystem.Directory.CreateDirectory(root);
            foreach (var folder in entries.Where(e => e.IsDirectory))
            {
                if (ExtractPaths.Resolve(root, folder.Path, out _) is { } made)
                {
                    fileSystem.Directory.CreateDirectory(made);
                }
            }

            progress.BeginArchive(entries.Where(e => !e.IsDirectory).Sum(e => e.Size));
            var sink = new Sink(this, context, progress, source, root, request.Conflict);
            try
            {
                await reader.ExtractAllAsync(path, request.ArchivePassword, sink, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveException ex)
            {
                sink.Abandon();
                context.AddResult(new ItemOutcome(source, root, ItemStatus.Failed, ex.Message));
                continue;
            }
            catch
            {
                sink.Abandon();
                throw;
            }

            extracted++;
            if (sink.Failed > 0)
            {
                context.AddNote($"{sink.Failed:N0} files from {fileSystem.Path.GetFileName(path)} could not be extracted; they are listed in the report.");
            }

            context.AddResult(new ItemOutcome(source, root, ItemStatus.Done));
        }

        progress.Finish();
        return extracted == 0 && context.Results.Count > 0 ? new JobOutcome(JobState.Failed, context.Results[0].Error ?? "Nothing was extracted.") : JobOutcome.Completed;
    }

    // "name" -> "name (2)", "name (3)", ... for the first one that is free.
    private string FreeName(string path, bool isFile)
    {
        var folder = fileSystem.Path.GetDirectoryName(path)!;
        var stem = isFile ? fileSystem.Path.GetFileNameWithoutExtension(path) : fileSystem.Path.GetFileName(path);
        var extension = isFile ? fileSystem.Path.GetExtension(path) : string.Empty;
        for (var i = 2; ; i++)
        {
            var candidate = fileSystem.Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!fileSystem.File.Exists(candidate) && !fileSystem.Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private sealed class Progress(JobContext context, long totalBytes, int totalFiles)
    {
        private long _bytes;
        private int _files;

        private long _archiveStart;
        private long _archiveBytes;

        public void BeginArchive(long archiveBytes)
        {
            _archiveStart = Volatile.Read(ref _bytes);
            _archiveBytes = archiveBytes;
        }

        public void Wrote(long count, string current)
        {
            var bytes = Interlocked.Add(ref _bytes, count);
            context.Report(bytes, totalBytes, Volatile.Read(ref _files), totalFiles, current);
        }

        // A solid archive is unpacked first and its files moved into place after: the unpacking is the work, and moving adds no bytes.
        public void Unpacking(int percent)
        {
            Volatile.Write(ref _bytes, _archiveStart + (_archiveBytes * Math.Clamp(percent, 0, 100) / 100));
            context.Report(Volatile.Read(ref _bytes), totalBytes, Volatile.Read(ref _files), totalFiles);
        }

        public void FileDone() => Interlocked.Increment(ref _files);

        public void Finish() => context.Report(Volatile.Read(ref _bytes), totalBytes, Volatile.Read(ref _files), totalFiles);
    }

    private sealed class Sink(ArchiveExtractExecutor owner, JobContext context, Progress progress, string source, string root, ConflictPolicy conflict) : IArchiveExtractionSink
    {
        private Target? _open;

        public string? StagingFolder => root;

        public void Unpacking(int percent) => progress.Unpacking(percent);

        public int Failed { get; private set; }

        public IExtractionTarget? Open(ArchiveEntryInfo entry)
        {
            _open?.Discard();
            var fs = owner.Files;
            if (ExtractPaths.Resolve(root, entry.Path, out var problem) is not { } target)
            {
                Failed++;
                context.AddResult(new ItemOutcome(source + "\\" + entry.Path.Replace('/', '\\'), null, ItemStatus.Failed, $"Not extracted: {problem}."));
                return null;
            }

            var replace = false;
            if (fs.File.Exists(target) || fs.Directory.Exists(target))
            {
                switch (conflict)
                {
                    case ConflictPolicy.Skip:
                        context.AddResult(new ItemOutcome(source + "\\" + entry.Path.Replace('/', '\\'), null, ItemStatus.Skipped, "A file with that name is already there."));
                        return null;
                    case ConflictPolicy.Replace when fs.File.Exists(target):
                        replace = true;
                        break;
                    default:
                        target = owner.FreeName(target, isFile: true);
                        break;
                }
            }

            try
            {
                fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(target)!);
                _open = new Target(owner, progress, entry, target, replace, this);
                return _open;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failed++;
                context.AddResult(new ItemOutcome(source + "\\" + entry.Path.Replace('/', '\\'), null, ItemStatus.Failed, ex.Message));
                return null;
            }
        }

        public void Abandon() => _open?.Discard();

        public void Fail(Target target, Exception? error)
        {
            Failed++;
            context.AddResult(new ItemOutcome(source + "\\" + target.Entry.Path.Replace('/', '\\'), null, ItemStatus.Failed, error?.Message ?? "The file could not be unpacked (wrong password or damaged data)."));
        }
    }

    private sealed class Target : IExtractionTarget
    {
        private readonly ArchiveExtractExecutor _owner;
        private readonly Progress _progress;
        private readonly string _target;
        private readonly string _part;
        private readonly bool _replace;
        private readonly Sink _sink;
        private bool _finished;

        public Target(ArchiveExtractExecutor owner, Progress progress, ArchiveEntryInfo entry, string target, bool replace, Sink sink)
        {
            _owner = owner;
            _progress = progress;
            Entry = entry;
            _target = target;
            _part = target + ".leviathan-part";
            _replace = replace;
            _sink = sink;
            Stream = new Counting(owner.Files.File.Create(_part), n => progress.Wrote(n, entry.Path));
        }

        public ArchiveEntryInfo Entry { get; }

        public Stream Stream { get; }

        public void Completed()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            Stream.Dispose();
            var fs = _owner.Files;
            try
            {
                fs.File.Move(_part, _target, overwrite: _replace);
                if (Entry.Modified is { } modified)
                {
                    fs.File.SetLastWriteTimeUtc(_target, modified.UtcDateTime);
                }

                _progress.FileDone();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Discard();
                _sink.Fail(this, ex);
            }
        }

        public bool TryAdopt(string stagedFile)
        {
            if (_finished)
            {
                return false;
            }

            var fs = _owner.Files;
            try
            {
                Stream.Dispose();
                if (fs.File.Exists(_part))
                {
                    fs.File.Delete(_part);
                }

                fs.File.Move(stagedFile, _target, overwrite: _replace);
                if (Entry.Modified is { } modified)
                {
                    fs.File.SetLastWriteTimeUtc(_target, modified.UtcDateTime);
                }

                _finished = true;
                _progress.FileDone();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not on the same volume, or the target is locked: the file is copied the ordinary way.
                return false;
            }
        }

        public void Failed(Exception? error)
        {
            if (_finished)
            {
                return;
            }

            Discard();
            _sink.Fail(this, error);
        }

        public void Discard()
        {
            if (_finished && !_owner.Files.File.Exists(_part))
            {
                return;
            }

            _finished = true;
            try
            {
                Stream.Dispose();
                if (_owner.Files.File.Exists(_part))
                {
                    _owner.Files.File.Delete(_part);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stray part file is harmless; it never has a real name.
            }
        }
    }

    private sealed class Counting(Stream inner, Action<long> wrote) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            wrote(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            wrote(buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
