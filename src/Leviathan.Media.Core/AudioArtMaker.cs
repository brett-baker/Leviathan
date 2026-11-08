using System.ComponentModel;
using System.Globalization;

namespace Leviathan.Media;

/// <summary>Which picture of an audio file is wanted.</summary>
public enum AudioArtKind
{
    /// <summary>The loudness of the whole file drawn as a waveform (spec §9.1: "waveform plus cover art").</summary>
    Waveform,

    /// <summary>The cover art embedded in the file (an attached picture, as in MP3, FLAC and M4A tags).</summary>
    Cover,
}

/// <summary>
/// Draws the pictures of an audio file the preview pane shows with the player (spec §9.1): a waveform of the whole file, or its
/// embedded cover art, with ffmpeg into the disk cache, as a JPEG. Written under a temporary name and renamed when complete so a
/// half-written picture is never served. A file without cover art is a normal failure with a plain message, and the waveform of a long
/// file is given a long time because ffmpeg has to decode all of it. Never throws for a bad file.
/// </summary>
public sealed class AudioArtMaker(IProcessRunner runner, Func<FfmpegTools> tools, ThumbnailDiskCache cache)
{
    private static readonly TimeSpan WaveformTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CoverTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The disk cache the pictures are written to.</summary>
    public ThumbnailDiskCache Cache => cache;

    /// <summary>The cache identity of a picture: the file version, the width, and the kind and height in the variant.</summary>
    public static ThumbnailKey KeyFor(AudioArtKind kind, string path, long size, long modifiedUtcTicks, int width, int height) =>
        new(path, size, modifiedUtcTicks, width, kind == AudioArtKind.Waveform ? string.Create(CultureInfo.InvariantCulture, $"wave{height}") : "cover");

    /// <summary>The ffmpeg arguments; public so the exact command is testable.</summary>
    public static IReadOnlyList<string> Arguments(AudioArtKind kind, string path, string output, int width, int height, bool isNetwork)
    {
        List<string> arguments = ["-hide_banner", "-v", "error", "-y"];
        if (isNetwork && kind == AudioArtKind.Cover)
        {
            // The cover is near the start of the file, so a share is read only as far as it takes to find it.
            arguments.AddRange(["-probesize", "4194304", "-analyzeduration", "3000000"]);
        }

        arguments.AddRange(["-i", "file:" + path]);
        if (kind == AudioArtKind.Waveform)
        {
            arguments.AddRange(["-vn", "-sn", "-dn", "-filter_complex",
                string.Create(CultureInfo.InvariantCulture, $"aformat=channel_layouts=mono,showwavespic=s={width}x{height}:colors=0x58a6ff")]);
        }
        else
        {
            arguments.AddRange(["-an", "-sn", "-dn", "-map", "0:v:0",
                "-vf", string.Create(CultureInfo.InvariantCulture, $"scale={width}:{width}:force_original_aspect_ratio=decrease")]);
        }

        arguments.AddRange(["-frames:v", "1", "-q:v", "3", "-f", "image2", "-update", "1", output]);
        return arguments;
    }

    /// <summary>The picture of <paramref name="key"/>: from the cache, or drawn now.</summary>
    public async Task<ThumbnailResult> MakeAsync(AudioArtKind kind, string path, ThumbnailKey key, int height, bool isNetwork, CancellationToken cancellationToken)
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
        try
        {
            ProcessOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(ffmpeg, Arguments(kind, path, temporary, key.Edge, height, isNetwork), kind == AudioArtKind.Waveform ? WaveformTimeout : CoverTimeout, 64 * 1024, cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                return new ThumbnailResult(ThumbnailStatus.ToolMissing, null, $"ffmpeg could not be started: {ex.Message}");
            }

            if (outcome.TimedOut)
            {
                return new ThumbnailResult(ThumbnailStatus.Failed, null, "ffmpeg took too long over this file.");
            }

            if (outcome.ExitCode == 0 && new FileInfo(temporary) is { Exists: true, Length: > 0 })
            {
                File.Move(temporary, target, overwrite: true);
                cache.TrimIfDue();
                return new ThumbnailResult(ThumbnailStatus.Ok, target, null);
            }

            var message = kind == AudioArtKind.Cover ? "This file has no cover art." : outcome.Error.Trim();
            return new ThumbnailResult(ThumbnailStatus.Failed, null, string.IsNullOrEmpty(message) ? "No picture could be drawn." : message);
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

/// <summary>Draws audio pictures one at a time (spec §3.1): a waveform decodes a whole file, so two at once would starve the rest of the media process.</summary>
public sealed class AudioArtService(AudioArtMaker maker) : IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _slot.Dispose();

    /// <summary>The picture; waits for its turn unless <paramref name="cancellationToken"/> is cancelled first.</summary>
    public async Task<ThumbnailResult> GetAsync(AudioArtKind kind, string path, long size, long modifiedUtcTicks, int width, int height, bool isNetwork, CancellationToken cancellationToken)
    {
        var key = AudioArtMaker.KeyFor(kind, path, size, modifiedUtcTicks, width, height);
        if (maker.Cache.TryGet(key) is { } cached)
        {
            return new ThumbnailResult(ThumbnailStatus.Ok, cached, null);
        }

        await _slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await maker.MakeAsync(kind, path, key, height, isNetwork, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slot.Release();
        }
    }
}
