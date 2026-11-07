using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Leviathan.Media;

/// <summary>Which version of which file a thumbnail is of.</summary>
/// <param name="Path">Win32 path.</param>
/// <param name="Size">Size in bytes (part of the identity: an edited file gets a new thumbnail).</param>
/// <param name="ModifiedUtcTicks">Last write time in UTC ticks.</param>
/// <param name="Edge">Longest edge of the picture in pixels.</param>
/// <param name="Variant">What else tells two pictures of the same file apart (the grid of a contact sheet); empty for a thumbnail.</param>
public readonly record struct ThumbnailKey(string Path, long Size, long ModifiedUtcTicks, int Edge, string Variant = "")
{
    /// <summary>A short stable name for the cache file.</summary>
    public string Hash
    {
        get
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"{Path.ToUpperInvariant()}|{Size}|{ModifiedUtcTicks}|{Edge}");
            if (Variant.Length > 0)
            {
                text += "|" + Variant;
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
        }
    }
}

/// <summary>The outcome of making a thumbnail.</summary>
public enum ThumbnailStatus
{
    /// <summary>The picture is in <see cref="ThumbnailResult.File"/>.</summary>
    Ok,

    /// <summary>ffmpeg could not draw a frame from the file.</summary>
    Failed,

    /// <summary>ffmpeg is not installed or could not be started.</summary>
    ToolMissing,
}

/// <summary>A thumbnail, or why there is none.</summary>
/// <param name="Status">What happened.</param>
/// <param name="File">The JPEG in the cache, for <see cref="ThumbnailStatus.Ok"/>.</param>
/// <param name="Message">Why it failed.</param>
public sealed record ThumbnailResult(ThumbnailStatus Status, string? File, string? Message);

/// <summary>
/// Thumbnails kept on disk as small JPEGs (spec §10.4, §3.1), named by <see cref="ThumbnailKey.Hash"/>, so a folder of videos is
/// drawn once and then from disk. The folder is size-capped: when it passes the cap the least recently used files are removed.
/// </summary>
public sealed class ThumbnailDiskCache(string folder, long capacityBytes = ThumbnailDiskCache.DefaultCapacityBytes)
{
    /// <summary>Default cap, 512 MB (about 30,000 thumbnails of 288 px).</summary>
    public const long DefaultCapacityBytes = 512L * 1024 * 1024;

    private readonly Lock _trimming = new();
    private int _sinceTrim;

    /// <summary>The cache folder.</summary>
    public string Folder => folder;

    /// <summary>Where the JPEG for <paramref name="key"/> lives (it may not exist).</summary>
    public string PathFor(ThumbnailKey key)
    {
        var hash = key.Hash;
        return Path.Combine(folder, hash[..2], hash + ".jpg");
    }

    /// <summary>The JPEG of <paramref name="key"/> when it is cached; marks it as used just now.</summary>
    public string? TryGet(ThumbnailKey key)
    {
        var file = PathFor(key);
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0)
            {
                return null;
            }

            info.LastAccessTimeUtc = DateTime.UtcNow;
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the least recently used files when the folder is over the cap; checks only every 50 new files.</summary>
    public void TrimIfDue()
    {
        if (Interlocked.Increment(ref _sinceTrim) % 50 != 0)
        {
            return;
        }

        Trim();
    }

    /// <summary>Removes the least recently used files until the folder is at 90% of the cap.</summary>
    public void Trim()
    {
        lock (_trimming)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                var files = new DirectoryInfo(folder).EnumerateFiles("*.jpg", SearchOption.AllDirectories)
                    .Select(f => (File: f, f.Length, Used: f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc))
                    .ToList();
                var total = files.Sum(f => f.Length);
                if (total <= capacityBytes)
                {
                    return;
                }

                var target = capacityBytes / 10 * 9;
                foreach (var (file, length, _) in files.OrderBy(f => f.Used))
                {
                    if (total <= target)
                    {
                        break;
                    }

                    try
                    {
                        file.Delete();
                        total -= length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // In use or already gone: the next trim tries again.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A cache that cannot be trimmed is still a cache.
            }
        }
    }
}

/// <summary>
/// Draws one frame of a video with ffmpeg into the disk cache (spec §10.4): a keyframe seek to about a tenth of the way in (the first
/// seconds are often black), scaled to fit the requested edge, written as a JPEG. Written under a temporary name and renamed when
/// complete so a half-written picture is never served. Never throws for a bad file: that is a <see cref="ThumbnailStatus.Failed"/>.
/// </summary>
public sealed class ThumbnailMaker(IProcessRunner runner, Func<FfmpegTools> tools, ThumbnailDiskCache cache)
{
    /// <summary>The disk cache thumbnails are written to.</summary>
    public ThumbnailDiskCache Cache => cache;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Seek used when the duration is not known.</summary>
    public static readonly TimeSpan DefaultSeek = TimeSpan.FromSeconds(10);

    /// <summary>The point in a video of <paramref name="duration"/> to take the frame from: a tenth in, between 1 s and 10 minutes.</summary>
    public static TimeSpan SeekFor(TimeSpan? duration) => duration is { } d && d > TimeSpan.Zero
        ? TimeSpan.FromSeconds(Math.Clamp(d.TotalSeconds / 10, 1, 600)).Clamp(d)
        : DefaultSeek;

    /// <summary>The ffmpeg arguments; public so the exact command is testable.</summary>
    public static IReadOnlyList<string> Arguments(string path, string output, TimeSpan seek, int edge, bool isNetwork)
    {
        List<string> arguments = ["-hide_banner", "-v", "error", "-y"];
        if (isNetwork)
        {
            // Read only as much as it takes to find the video, as the probe does.
            arguments.AddRange(["-probesize", "4194304", "-analyzeduration", "3000000"]);
        }

        // -ss before -i seeks by keyframe without decoding what it skips; "file:" stops a name being read as a protocol.
        arguments.AddRange(["-ss", seek.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", "file:" + path]);
        arguments.AddRange(["-frames:v", "1", "-an", "-sn", "-dn",
            "-vf", string.Create(CultureInfo.InvariantCulture, $"scale={edge}:{edge}:force_original_aspect_ratio=decrease"),
            "-q:v", "5", "-f", "image2", "-update", "1", output]);
        return arguments;
    }

    /// <summary>The thumbnail of <paramref name="key"/>: from the cache, or drawn now.</summary>
    /// <param name="key">The file version and size.</param>
    /// <param name="isNetwork">True for a file on a network drive.</param>
    /// <param name="duration">The video's duration when known, to choose the frame.</param>
    /// <param name="cancellationToken">Cancels (and stops ffmpeg).</param>
    public async Task<ThumbnailResult> MakeAsync(ThumbnailKey key, bool isNetwork, TimeSpan? duration, CancellationToken cancellationToken)
    {
        if (cache.TryGet(key) is { } cached)
        {
            return new ThumbnailResult(ThumbnailStatus.Ok, cached, null);
        }

        if (tools().FfmpegPath is not { } ffmpeg)
        {
            return new ThumbnailResult(ThumbnailStatus.ToolMissing, null, "ffmpeg was not found. Set the ffmpeg folder in Settings.");
        }

        var target = cache.PathFor(key);
        var temporary = target + ".part.jpg";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var seek = SeekFor(duration);
        string? last = null;
        try
        {
            // A seek past the end of a short or damaged file draws nothing: try again from the start before giving up.
            foreach (var at in seek > TimeSpan.Zero ? new[] { seek, TimeSpan.Zero } : [TimeSpan.Zero])
            {
                ProcessOutcome outcome;
                try
                {
                    outcome = await runner.RunAsync(ffmpeg, Arguments(key.Path, temporary, at, key.Edge, isNetwork), Timeout, 64 * 1024, cancellationToken).ConfigureAwait(false);
                }
                catch (Win32Exception ex)
                {
                    return new ThumbnailResult(ThumbnailStatus.ToolMissing, null, $"ffmpeg could not be started: {ex.Message}");
                }

                if (outcome.TimedOut)
                {
                    return new ThumbnailResult(ThumbnailStatus.Failed, null, $"ffmpeg did not answer within {Timeout.TotalSeconds:0} s.");
                }

                last = outcome.Error.Trim();
                if (outcome.ExitCode == 0 && new FileInfo(temporary) is { Exists: true, Length: > 0 })
                {
                    File.Move(temporary, target, overwrite: true);
                    cache.TrimIfDue();
                    return new ThumbnailResult(ThumbnailStatus.Ok, target, null);
                }
            }

            return new ThumbnailResult(ThumbnailStatus.Failed, null, string.IsNullOrEmpty(last) ? "No frame could be drawn." : last);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover .part.jpg is not served and is removed by the next trim of an old cache.
            }
        }
    }
}

internal static class TimeSpanExtensions
{
    public static TimeSpan Clamp(this TimeSpan value, TimeSpan max) => value > max ? max : value;
}

/// <summary>
/// Draws thumbnails two at a time (spec §3.1): a screen of videos asks for many at once, and each is a process reading a file.
/// A request that is still waiting when its caller leaves (the row scrolled away) never starts.
/// </summary>
public sealed class ThumbnailService(ThumbnailMaker maker, int maxParallel = 2) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(maxParallel, maxParallel);
    private int _running;
    private long _made;
    private long _failed;

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    /// <summary>Thumbnails being drawn now.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Thumbnails drawn since the process started.</summary>
    public long Made => Interlocked.Read(ref _made);

    /// <summary>Files no frame could be drawn from since the process started.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>The cached JPEG of <paramref name="key"/>, or null (touches it as used).</summary>
    public string? CachedFile(ThumbnailKey key) => maker.Cache.TryGet(key);

    /// <summary>The thumbnail of <paramref name="key"/>; waits for a free slot unless <paramref name="cancellationToken"/> is cancelled first.</summary>
    public async Task<ThumbnailResult> GetAsync(ThumbnailKey key, bool isNetwork, TimeSpan? duration, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _running);
        try
        {
            var result = await maker.MakeAsync(key, isNetwork, duration, cancellationToken).ConfigureAwait(false);
            if (result.Status == ThumbnailStatus.Ok)
            {
                Interlocked.Increment(ref _made);
            }
            else if (result.Status == ThumbnailStatus.Failed)
            {
                Interlocked.Increment(ref _failed);
            }

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _slots.Release();
        }
    }
}
