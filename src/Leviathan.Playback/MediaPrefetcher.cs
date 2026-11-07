using System.Buffers.Binary;
using System.Diagnostics;

namespace Leviathan.Playback;

/// <summary>A span of a file to read ahead.</summary>
/// <param name="Offset">First byte.</param>
/// <param name="Length">Number of bytes.</param>
public readonly record struct ByteRange(long Offset, long Length);

/// <summary>What a prefetch did.</summary>
/// <param name="Ranges">The spans that were asked for.</param>
/// <param name="BytesRead">How many bytes were actually read.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Error">Why it stopped early (an unreachable share, a vanished file); null when it finished.</param>
public sealed record PrefetchResult(IReadOnlyList<ByteRange> Ranges, long BytesRead, TimeSpan Elapsed, string? Error);

/// <summary>
/// Reads the part of a video a player needs before it can start, so that the share (and the system cache) has it ready when playback
/// begins (spec §10.1: "Pre-buffering the next file reads its header and index, about 8 MB; for MP4 files that keep the index at
/// the end, the tail too"). A video is never copied and never read whole: this touches a few megabytes at the start and, where the
/// index lives at the end, a few at the end. It never throws for a file that cannot be read; a dead share is a
/// <see cref="PrefetchResult.Error"/>, bounded by a timeout (spec §3.3).
/// </summary>
public static class MediaPrefetcher
{
    /// <summary>Bytes read from the start of a file.</summary>
    public const long HeadBytes = 4L << 20;

    /// <summary>Most bytes read from where an MP4's <c>moov</c> index sits.</summary>
    public const long IndexBytes = 4L << 20;

    /// <summary>Bytes read from the end of a file that is not an MP4 (Matroska cues, an AVI index).</summary>
    public const long TailBytes = 1L << 20;

    /// <summary>How long a prefetch may take before it gives up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const int MaxBoxesWalked = 64;

    private static readonly HashSet<string> Mp4Family = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".m4a", ".3gp", ".3g2" };

    /// <summary>True for the extensions whose index is a <c>moov</c> box that may sit at the end.</summary>
    public static bool IsMp4Family(string extension) => Mp4Family.Contains(extension);

    /// <summary>
    /// The spans to read for a file of <paramref name="length"/> bytes: the head, and the index when it is not inside the head.
    /// For an MP4 the index is found by walking the top-level boxes (a handful of 8-byte reads, however large the file); for anything
    /// else the last <see cref="TailBytes"/> are taken, which is where Matroska and AVI keep theirs.
    /// </summary>
    public static IReadOnlyList<ByteRange> Plan(Stream stream, string extension, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (length <= 0)
        {
            return [];
        }

        var head = new ByteRange(0, Math.Min(length, HeadBytes));
        if (length <= HeadBytes)
        {
            return [head];
        }

        if (IsMp4Family(extension))
        {
            return FindMoov(stream, length) is { } moov && moov.Offset + moov.Length > head.Length
                ? [head, ClampIndex(moov, head, length)]
                : [head];
        }

        var tailStart = Math.Max(head.Length, length - TailBytes);
        return [head, new ByteRange(tailStart, length - tailStart)];
    }

    /// <summary>Reads the planned spans of <paramref name="path"/> and throws the bytes away.</summary>
    public static async Task<PrefetchResult> PrefetchAsync(string path, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<ByteRange> ranges = [];
        long read = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            // Open for sharing so a file being written or played elsewhere can still be warmed.
            var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });
            await using var _ = stream.ConfigureAwait(false);
            ranges = Plan(stream, Path.GetExtension(path), stream.Length);
            var buffer = new byte[256 * 1024];
            foreach (var range in ranges)
            {
                stream.Seek(range.Offset, SeekOrigin.Begin);
                for (var left = range.Length; left > 0;)
                {
                    var got = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), limit.Token).ConfigureAwait(false);
                    if (got == 0)
                    {
                        break;
                    }

                    read += got;
                    left -= got;
                }
            }

            return new PrefetchResult(ranges, read, clock.Elapsed, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PrefetchResult(ranges, read, clock.Elapsed, "The file did not answer in time.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new PrefetchResult(ranges, read, clock.Elapsed, ex.Message);
        }
    }

    // The index may be huge on a long recording; the first IndexBytes of it are what playback needs to start.
    private static ByteRange ClampIndex(ByteRange moov, ByteRange head, long length)
    {
        var start = Math.Max(moov.Offset, head.Length);
        return new ByteRange(start, Math.Min(Math.Min(moov.Length - (start - moov.Offset), IndexBytes), length - start));
    }

    // Walks the top-level boxes (size, type) from the start until moov turns up: ftyp, then usually moov or a long mdat and moov.
    private static ByteRange? FindMoov(Stream stream, long length)
    {
        Span<byte> header = stackalloc byte[16];
        long offset = 0;
        for (var i = 0; i < MaxBoxesWalked && offset + 8 <= length; i++)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            if (stream.Read(header[..8]) < 8)
            {
                return null;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));
            if (size == 1)
            {
                // A 64-bit size follows the type, for boxes over 4 GB such as a large mdat.
                if (stream.Read(header.Slice(8, 8)) < 8)
                {
                    return null;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            }
            else if (size == 0)
            {
                size = length - offset;
            }

            if (size < 8)
            {
                return null;
            }

            if (type == "moov")
            {
                return new ByteRange(offset, Math.Min(size, length - offset));
            }

            offset += size;
        }

        return null;
    }
}
