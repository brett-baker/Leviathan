using System.ComponentModel;
using System.Globalization;

namespace Leviathan.Media;

/// <summary>The shape of a contact sheet: a grid of equally sized frames (spec §10.4: 3×3 to 6×6).</summary>
/// <param name="Columns">Frames per row, 3 to 6.</param>
/// <param name="Rows">Rows, 3 to 6.</param>
/// <param name="CellWidth">Width of one frame in pixels.</param>
/// <param name="CellHeight">Height of one frame in pixels (the video's proportions, rounded to an even number).</param>
public sealed record ContactSheetSpec(int Columns, int Rows, int CellWidth, int CellHeight)
{
    /// <summary>Smallest grid edge.</summary>
    public const int MinGrid = 3;

    /// <summary>Largest grid edge.</summary>
    public const int MaxGrid = 6;

    /// <summary>Frames in the sheet.</summary>
    public int Count => Columns * Rows;

    /// <summary>Width of the whole sheet.</summary>
    public int Width => Columns * CellWidth;

    /// <summary>Height of the whole sheet.</summary>
    public int Height => Rows * CellHeight;

    /// <summary>
    /// A spec for a video of <paramref name="videoWidth"/> × <paramref name="videoHeight"/> (16:9 when either is unknown), with the
    /// grid kept to 3 to 6 and the cell width to 96 to 480 pixels.
    /// </summary>
    public static ContactSheetSpec For(int columns, int rows, int cellWidth, int? videoWidth, int? videoHeight)
    {
        var width = Math.Clamp(cellWidth, 96, 480) / 2 * 2;
        var ratio = videoWidth is > 0 && videoHeight is > 0 ? (double)videoHeight.Value / videoWidth.Value : 9.0 / 16.0;

        // A very tall or very wide video would make a silly sheet: keep the proportions within 1:3 to 3:1.
        ratio = Math.Clamp(ratio, 1.0 / 3, 3.0);
        var height = Math.Max(2, (int)Math.Round(width * ratio / 2) * 2);
        return new ContactSheetSpec(Math.Clamp(columns, MinGrid, MaxGrid), Math.Clamp(rows, MinGrid, MaxGrid), width, height);
    }

    /// <summary>
    /// The moments the frames are taken at, row by row: the middle of each of <see cref="Count"/> equal parts of the first 95% of
    /// <paramref name="duration"/>, so the first and last frames are never the black opening or the credits' end, and a click on
    /// frame <c>i</c> can start playback at <c>FrameTimes[i]</c>.
    /// </summary>
    public IReadOnlyList<TimeSpan> FrameTimes(TimeSpan duration)
    {
        // The container's length can be longer than its last frame (audio running on), and a seek past the last frame draws nothing, which
        // would leave a hole in the grid (ffmpeg then writes no sheet at all): so the parts cover 95% of the length and no frame is
        // taken in the last half second.
        var latest = Math.Max(0, duration.TotalSeconds - 0.5);
        return [.. Enumerable.Range(0, Count).Select(i => TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds * 0.95 * (i + 0.5) / Count, latest)))];
    }
}

/// <summary>A contact sheet, or why there is none.</summary>
/// <param name="Status">What happened.</param>
/// <param name="File">The JPEG in the cache.</param>
/// <param name="Spec">The grid.</param>
/// <param name="Times">When each frame was taken, row by row.</param>
/// <param name="Message">Why it failed.</param>
public sealed record ContactSheetResult(ThumbnailStatus Status, string? File, ContactSheetSpec? Spec, IReadOnlyList<TimeSpan> Times, string? Message);

/// <summary>
/// Draws a contact sheet with one ffmpeg run (spec §10.4): every frame is its own keyframe seek (<c>-ss</c> before <c>-i</c>, so
/// nothing between the frames is decoded), scaled into its cell and stacked into the grid, written as one JPEG in the disk cache.
/// The file is written under a temporary name and renamed when complete. A bad file is a <see cref="ThumbnailStatus.Failed"/>,
/// never an exception.
/// </summary>
public sealed class ContactSheetMaker(IProcessRunner runner, Func<FfmpegTools> tools, ThumbnailDiskCache cache)
{
    private static readonly TimeSpan BaseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The disk cache sheets are written to.</summary>
    public ThumbnailDiskCache Cache => cache;

    /// <summary>The cache key of a sheet: the file version and the grid are the identity.</summary>
    public static ThumbnailKey KeyFor(string path, long size, long modifiedUtcTicks, ContactSheetSpec spec) =>
        new(path, size, modifiedUtcTicks, spec.CellWidth, string.Create(CultureInfo.InvariantCulture, $"sheet{spec.Columns}x{spec.Rows}h{spec.CellHeight}"));

    /// <summary>The ffmpeg arguments; public so the exact command is testable.</summary>
    public static IReadOnlyList<string> Arguments(string path, string output, ContactSheetSpec spec, IReadOnlyList<TimeSpan> times, bool isNetwork)
    {
        List<string> arguments = ["-hide_banner", "-v", "error", "-y"];
        foreach (var time in times)
        {
            if (isNetwork)
            {
                arguments.AddRange(["-probesize", "4194304", "-analyzeduration", "3000000"]);
            }

            arguments.AddRange(["-ss", time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", "file:" + path]);
        }

        var graph = new System.Text.StringBuilder();
        for (var i = 0; i < times.Count; i++)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[{i}:v:0]scale={spec.CellWidth}:{spec.CellHeight}:force_original_aspect_ratio=decrease,pad={spec.CellWidth}:{spec.CellHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1[f{i}];");
        }

        for (var i = 0; i < times.Count; i++)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[f{i}]");
        }

        var layout = string.Join('|', Enumerable.Range(0, times.Count).Select(i => string.Create(CultureInfo.InvariantCulture, $"{i % spec.Columns * spec.CellWidth}_{i / spec.Columns * spec.CellHeight}")));
        graph.Append(CultureInfo.InvariantCulture, $"xstack=inputs={times.Count}:layout={layout}[sheet]");
        arguments.AddRange(["-filter_complex", graph.ToString(), "-map", "[sheet]", "-frames:v", "1", "-pix_fmt", "yuvj420p", "-q:v", "4", "-f", "image2", "-update", "1", output]);
        return arguments;
    }

    /// <summary>The sheet of a video: from the cache, or drawn now.</summary>
    /// <param name="path">Win32 path of the video.</param>
    /// <param name="size">Size in bytes.</param>
    /// <param name="modifiedUtcTicks">Last write time in UTC ticks.</param>
    /// <param name="isNetwork">True for a file on a network drive.</param>
    /// <param name="duration">The video's duration; without it there is no way to space the frames.</param>
    /// <param name="spec">The grid.</param>
    /// <param name="cancellationToken">Cancels (and stops ffmpeg).</param>
    public async Task<ContactSheetResult> MakeAsync(string path, long size, long modifiedUtcTicks, bool isNetwork, TimeSpan? duration, ContactSheetSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (duration is not { } length || length <= TimeSpan.Zero)
        {
            return new ContactSheetResult(ThumbnailStatus.Failed, null, spec, [], "The length of the video is not known, so its frames cannot be spaced out.");
        }

        var times = spec.FrameTimes(length);
        var key = KeyFor(path, size, modifiedUtcTicks, spec);
        if (cache.TryGet(key) is { } cached)
        {
            return new ContactSheetResult(ThumbnailStatus.Ok, cached, spec, times, null);
        }

        if (tools().FfmpegPath is not { } ffmpeg)
        {
            return new ContactSheetResult(ThumbnailStatus.ToolMissing, null, spec, times, "ffmpeg was not found. Set the ffmpeg folder in Settings.");
        }

        var target = cache.PathFor(key);
        var temporary = target + ".part.jpg";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            ProcessOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(ffmpeg, Arguments(path, temporary, spec, times, isNetwork), BaseTimeout + TimeSpan.FromSeconds(spec.Count), 64 * 1024, cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                return new ContactSheetResult(ThumbnailStatus.ToolMissing, null, spec, times, $"ffmpeg could not be started: {ex.Message}");
            }

            if (outcome.TimedOut)
            {
                return new ContactSheetResult(ThumbnailStatus.Failed, null, spec, times, "ffmpeg did not finish the contact sheet in time.");
            }

            if (outcome.ExitCode == 0 && new FileInfo(temporary) is { Exists: true, Length: > 0 })
            {
                File.Move(temporary, target, overwrite: true);
                cache.TrimIfDue();
                return new ContactSheetResult(ThumbnailStatus.Ok, target, spec, times, null);
            }

            var reason = outcome.Error.Trim();
            return new ContactSheetResult(ThumbnailStatus.Failed, null, spec, times, string.IsNullOrEmpty(reason) ? "No contact sheet could be drawn." : reason);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover .part.jpg is never served.
            }
        }
    }
}

/// <summary>
/// Draws contact sheets one at a time (spec §3.1): each is one process with a seek per frame, much heavier than a thumbnail, and a
/// folder of them runs in the background behind whatever the user is looking at. A request that is still waiting when its caller
/// leaves never starts.
/// </summary>
public sealed class ContactSheetService(ContactSheetMaker maker, int maxParallel = 1) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(maxParallel, maxParallel);
    private int _running;
    private int _waiting;
    private long _made;

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    /// <summary>Sheets being drawn now.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Sheets waiting for their turn.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Sheets drawn since the process started.</summary>
    public long Made => Interlocked.Read(ref _made);

    /// <summary>The cached sheet of a video, or null (touches it as used).</summary>
    public string? CachedFile(string path, long size, long modifiedUtcTicks, ContactSheetSpec spec) =>
        maker.Cache.TryGet(ContactSheetMaker.KeyFor(path, size, modifiedUtcTicks, spec));

    /// <summary>The sheet; waits for its turn unless <paramref name="cancellationToken"/> is cancelled first.</summary>
    public async Task<ContactSheetResult> GetAsync(string path, long size, long modifiedUtcTicks, bool isNetwork, TimeSpan? duration, ContactSheetSpec spec, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        Interlocked.Increment(ref _running);
        try
        {
            var result = await maker.MakeAsync(path, size, modifiedUtcTicks, isNetwork, duration, spec, cancellationToken).ConfigureAwait(false);
            if (result.Status == ThumbnailStatus.Ok)
            {
                Interlocked.Increment(ref _made);
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
