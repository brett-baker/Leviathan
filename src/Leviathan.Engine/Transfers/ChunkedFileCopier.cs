using System.IO.Abstractions;
using System.IO.Hashing;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// The portable chunked copier (spec §12.1): reads and writes through this machine in buffers sized from measured throughput, into
/// <c>name.leviathan-part</c>, preserving timestamps, attributes and alternate streams, and renames when complete. Files at or above
/// <c>unbufferedThreshold</c> (256 MB by default) are copied with unbuffered I/O, so a 100 GB file does not flood the file cache and
/// evict everything else. Unbuffered I/O needs sector-aligned memory and sector-sized writes: the buffer is aligned by hand and the
/// last, partial sector is written padded and the file is then cut to its real length.
/// </summary>
public sealed class ChunkedFileCopier : IFileCopier
{
    /// <summary>Files this size or larger use unbuffered I/O (spec §12.1 step 2).</summary>
    public const long DefaultUnbufferedThreshold = 256L * 1024 * 1024;

    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int Sector = 4096;

    private readonly IFileSystem _fs;
    private readonly Func<BufferPolicy> _policy;
    private readonly IAlternateStreams _streams;
    private readonly long _unbufferedThreshold;
    private readonly ThroughputMemory _memory;
    private readonly TimeProvider _time;
    private readonly long _confirmEveryBytes;

    /// <summary>The part file is flushed to disk and reported to the journal after this many bytes (or two seconds, whichever comes first).</summary>
    public const long DefaultConfirmEveryBytes = 32L * 1024 * 1024;

    /// <summary>Creates the copier.</summary>
    /// <param name="fileSystem">The file system to copy on.</param>
    /// <param name="policy">Makes the buffer policy for each file; null = adaptive, starting from what was measured for the destination.</param>
    /// <param name="streams">Alternate stream support; null = none.</param>
    /// <param name="unbufferedThreshold">Files at least this long use unbuffered I/O.</param>
    /// <param name="memory">Throughput remembered per destination; null = private to this copier.</param>
    /// <param name="time">Clock for throughput measurements and confirmation timing.</param>
    /// <param name="confirmEveryBytes">How much is written between flushes to disk that a resume can rely on.</param>
    public ChunkedFileCopier(
        IFileSystem fileSystem,
        Func<BufferPolicy>? policy = null,
        IAlternateStreams? streams = null,
        long unbufferedThreshold = DefaultUnbufferedThreshold,
        ThroughputMemory? memory = null,
        TimeProvider? time = null,
        long confirmEveryBytes = DefaultConfirmEveryBytes)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _confirmEveryBytes = confirmEveryBytes;
        _fs = fileSystem;
        _memory = memory ?? new ThroughputMemory();
        _policy = policy ?? (() => new BufferPolicy());
        _streams = streams ?? new NoAlternateStreams();
        _unbufferedThreshold = unbufferedThreshold;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<FileCopyResult> CopyAsync(FileCopyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var part = request.Target + CopyMoveExecutor.PartSuffix;
        var info = _fs.FileInfo.New(request.Source);
        var length = info.Length;
        var unbuffered = length >= _unbufferedThreshold;
        var root = _fs.Path.GetPathRoot(request.Target) ?? string.Empty;
        var policy = _policy();
        if (policy.BytesPerSecond <= 0)
        {
            policy.Observe((long)_memory.Get(root), TimeSpan.FromSeconds(1));
        }

        byte[]? sourceHash = null;
        var streamsMissed = 0;
        try
        {
            var options = FileOptions.SequentialScan | (unbuffered ? NoBuffering : FileOptions.None);
            var resumeFrom = ResumePoint(request, part, length, unbuffered);
            using (var input = _fs.FileStream.New(request.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 0, options))
            using (var output = _fs.FileStream.New(part, resumeFrom > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.None, 0, options))
            {
                var hash = request.ComputeSourceHash ? new XxHash3() : null;
                var size = unbuffered ? Align(policy.Size) : policy.Size;
                var buffer = new AlignedBuffer(size);
                if (resumeFrom > 0)
                {
                    // Continue after the bytes the journal says are on disk: cut the part file back to them, and read the same prefix of the
                    // source through the hash so a move still checks the whole file.
                    output.SetLength(resumeFrom);
                    output.Seek(resumeFrom, SeekOrigin.Begin);
                    if (hash is not null)
                    {
                        for (var left = resumeFrom; left > 0;)
                        {
                            var got = await input.ReadAsync(buffer.Memory[..(int)Math.Min(left, buffer.Size)], cancellationToken).ConfigureAwait(false);
                            if (got == 0)
                            {
                                throw new IOException($"{request.Source} is shorter than the part already copied.");
                            }

                            hash.Append(buffer.Memory.Span[..got]);
                            left -= got;
                        }
                    }
                    else
                    {
                        input.Seek(resumeFrom, SeekOrigin.Begin);
                    }

                    request.OnBytes(resumeFrom);
                }

                var written = resumeFrom;
                var sinceConfirm = 0L;
                var lastConfirm = _time.GetTimestamp();
                var padded = false;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var started = _time.GetTimestamp();
                    var read = await input.ReadAsync(buffer.Memory, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (padded)
                    {
                        throw new IOException($"{request.Source} returned more data after a short read.");
                    }

                    hash?.Append(buffer.Memory.Span[..read]);
                    var toWrite = read;
                    if (unbuffered && read % Sector != 0)
                    {
                        toWrite = Align(read);
                        buffer.Memory.Span[read..toWrite].Clear();
                        padded = true;
                    }

                    await output.WriteAsync(buffer.Memory[..toWrite], cancellationToken).ConfigureAwait(false);
                    request.OnBytes(read);
                    written += read;
                    sinceConfirm += read;
                    policy.Observe(read, _time.GetElapsedTime(started));
                    if (request.Throttle is not null)
                    {
                        await request.Throttle(read, cancellationToken).ConfigureAwait(false);
                    }

                    if (!padded && request.OnConfirmed is not null
                        && (sinceConfirm >= _confirmEveryBytes || _time.GetElapsedTime(lastConfirm) >= TimeSpan.FromSeconds(2)))
                    {
                        output.Flush(flushToDisk: true);
                        request.OnConfirmed(written);
                        sinceConfirm = 0;
                        lastConfirm = _time.GetTimestamp();
                    }

                    var next = unbuffered ? Align(policy.Size) : policy.Size;
                    if (next != buffer.Size)
                    {
                        buffer = new AlignedBuffer(next);
                    }
                }

                if (padded)
                {
                    output.SetLength(length);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                sourceHash = hash?.GetCurrentHash();
            }

            _memory.Set(root, policy.BytesPerSecond);
            var onDisk = _fs.FileInfo.New(part).Length;
            if (onDisk != length)
            {
                throw new IOException($"The copy of {_fs.Path.GetFileName(request.Source)} is {onDisk:N0} bytes, not {length:N0}.");
            }

            foreach (var name in _streams.Names(request.Source))
            {
                try
                {
                    await _streams.CopyAsync(request.Source, part, name, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // The destination (a share or file system without streams) cannot take this one: the data is intact, say so in the result.
                    streamsMissed++;
                }
            }

            _fs.File.SetLastWriteTimeUtc(part, info.LastWriteTimeUtc);
            _fs.File.SetCreationTimeUtc(part, info.CreationTimeUtc);
            _fs.File.SetAttributes(part, info.Attributes & ~FileAttributes.ReadOnly);
            _fs.File.Move(part, request.Target, overwrite: true);
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
            {
                _fs.File.SetAttributes(request.Target, info.Attributes);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || (ex is IOException io && IsTransient(io)))
        {
            // Paused, cancelled, or the network dropped: the part file and the journal's confirmed byte count let the next run continue.
            throw;
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        return new FileCopyResult(length, sourceHash, streamsMissed);
    }

    private static bool IsTransient(IOException exception) =>
        Core.Providers.LocationErrors.Wrap(exception, Core.Paths.LocationPath.Parse(@"\\server\share")).IsTransient;

    /// <summary>How many bytes of an earlier part file can be kept: what the journal confirmed, if the part file still holds them.</summary>
    private long ResumePoint(FileCopyRequest request, string part, long length, bool unbuffered)
    {
        if (request.ResumeFrom <= 0 || !_fs.File.Exists(part))
        {
            return 0;
        }

        var point = Math.Min(Math.Min(request.ResumeFrom, _fs.FileInfo.New(part).Length), length);
        return unbuffered ? point / Sector * Sector : point;
    }

    private static int Align(int size) => (size + Sector - 1) / Sector * Sector;

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

    /// <summary>A pinned buffer whose usable part starts on a sector boundary, as unbuffered I/O requires.</summary>
    private sealed class AlignedBuffer
    {
        public AlignedBuffer(int size)
        {
            Size = size;
            var array = GC.AllocateUninitializedArray<byte>(size + Sector, pinned: true);
            var address = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(array, 0);
            var offset = (int)((Sector - (address % Sector)) % Sector);
            Memory = array.AsMemory(offset, size);
        }

        public int Size { get; }

        public Memory<byte> Memory { get; }
    }
}
