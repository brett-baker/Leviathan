using System.Collections.Concurrent;

namespace Leviathan.Engine.Transfers;

/// <summary>One file to copy.</summary>
/// <param name="Source">Source path in the form the file system wants (for example <c>\\?\C:\…</c>).</param>
/// <param name="Target">Final destination path. Data is written to <c>Target + .leviathan-part</c> and renamed when complete.</param>
/// <param name="Path">The path the planner chose (spec §12.1 step 2); a copier may handle only some.</param>
/// <param name="ComputeSourceHash">True when the caller wants a SHA-256 of what was read, computed while copying (a move to another volume checks the copy against it).</param>
/// <param name="OnBytes">Called with the number of bytes just written, so progress and speed stay live inside a big file.</param>
public sealed record FileCopyRequest(string Source, string Target, TransferPath Path, bool ComputeSourceHash, Action<long> OnBytes);

/// <summary>What a finished file copy produced.</summary>
/// <param name="Length">Bytes copied.</param>
/// <param name="SourceHash">SHA-256 of the source as read, or null when not requested or not available (server-side copy).</param>
/// <param name="StreamsNotCopied">Alternate data streams the destination could not take (a share or file system without streams).</param>
public sealed record FileCopyResult(long Length, byte[]? SourceHash, int StreamsNotCopied = 0);

/// <summary>
/// Copies one file safely (spec §12.1 steps 3 and 5): into <c>name.leviathan-part</c>, preserving timestamps, attributes and alternate
/// data streams, then renamed into place, so a half-copied file never looks finished. Behind an interface so the Windows copier
/// (<c>CopyFile2</c>, unbuffered I/O) and the portable one are interchangeable and the logic around them is testable.
/// </summary>
public interface IFileCopier
{
    /// <summary>Copies the file. Throws <see cref="IOException"/> (or a <see cref="Core.Providers.LocationException"/>) on failure and leaves no part file behind.</summary>
    Task<FileCopyResult> CopyAsync(FileCopyRequest request, CancellationToken cancellationToken);
}

/// <summary>Reads and writes the alternate data streams of a file, where the platform has them (colour tags live there, spec §11).</summary>
public interface IAlternateStreams
{
    /// <summary>Names of the named streams of <paramref name="path"/>, without the default data stream.</summary>
    IReadOnlyList<string> Names(string path);

    /// <summary>Copies the named stream from <paramref name="source"/> to <paramref name="target"/>.</summary>
    Task CopyAsync(string source, string target, string name, CancellationToken cancellationToken);
}

/// <summary>No alternate streams: nothing to list, nothing to copy.</summary>
public sealed class NoAlternateStreams : IAlternateStreams
{
    /// <inheritdoc />
    public IReadOnlyList<string> Names(string path) => [];

    /// <inheritdoc />
    public Task CopyAsync(string source, string target, string name, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Chooses the copy buffer size from measured throughput instead of assuming a link speed (spec §3.5 item 3): 1 MB at slow speeds,
/// 2 to 4 MB around 1 Gb/s, up to 8 MB when the link is faster. The size is a power of two between <see cref="MinSize"/> and
/// <see cref="MaxSize"/>, based on a smoothed bytes-per-second figure.
/// </summary>
public sealed class BufferPolicy
{
    private double _bytesPerSecond;

    /// <summary>Smallest buffer.</summary>
    public const int MinSize = 1024 * 1024;

    /// <summary>Largest buffer.</summary>
    public const int MaxSize = 8 * 1024 * 1024;

    private readonly int? _fixed;

    /// <summary>Creates an adaptive policy starting from <paramref name="initialBytesPerSecond"/> (0 = unknown, starts at 1 MB).</summary>
    public BufferPolicy(double initialBytesPerSecond = 0) => _bytesPerSecond = initialBytesPerSecond;

    private BufferPolicy(int size) => _fixed = size;

    /// <summary>A policy that always answers <paramref name="size"/> bytes (tests, and callers that know better).</summary>
    public static BufferPolicy Fixed(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        return new BufferPolicy(size);
    }

    /// <summary>Smoothed throughput, bytes per second; 0 before any observation.</summary>
    public double BytesPerSecond => _bytesPerSecond;

    /// <summary>The buffer size to use now.</summary>
    public int Size
    {
        get
        {
            if (_fixed is { } size)
            {
                return size;
            }

            var wanted = _bytesPerSecond / 64;
            var buffer = MinSize;
            while (buffer < wanted && buffer < MaxSize)
            {
                buffer <<= 1;
            }

            return buffer;
        }
    }

    /// <summary>Feeds one measurement: <paramref name="bytes"/> moved in <paramref name="elapsed"/>.</summary>
    public void Observe(long bytes, TimeSpan elapsed)
    {
        if (_fixed is not null || bytes <= 0 || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var rate = bytes / elapsed.TotalSeconds;
        _bytesPerSecond = _bytesPerSecond <= 0 ? rate : (_bytesPerSecond * 0.8) + (rate * 0.2);
    }
}

/// <summary>Remembers the throughput last measured per destination (share or drive), so the next file starts with the right buffer.</summary>
public sealed class ThroughputMemory
{
    private readonly ConcurrentDictionary<string, double> _rates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last smoothed rate for <paramref name="destinationRoot"/>, or 0.</summary>
    public double Get(string destinationRoot) => _rates.GetValueOrDefault(destinationRoot);

    /// <summary>Stores a measured rate.</summary>
    public void Set(string destinationRoot, double bytesPerSecond)
    {
        if (bytesPerSecond > 0)
        {
            _rates[destinationRoot] = bytesPerSecond;
        }
    }
}
