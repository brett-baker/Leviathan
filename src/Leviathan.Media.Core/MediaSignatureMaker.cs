using System.ComponentModel;
using System.Globalization;

namespace Leviathan.Media;

/// <summary>A signature, or why there is none.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Signature">The signature for <see cref="ThumbnailStatus.Ok"/>.</param>
/// <param name="Message">Why it failed.</param>
public sealed record SignatureResult(ThumbnailStatus Status, MediaSignature? Signature, string? Message);

/// <summary>
/// Makes the signature of a picture or a video (spec §15.2) with ffmpeg: each frame is shrunk to 9×8 grey pixels and written as 72 raw
/// bytes to a temporary file (the process runner only carries text), which is read, hashed and deleted. A picture is one run with one
/// frame; a video is one run that seeks to five places (10% to 90% of its length) and writes five frames, so a long file is not opened
/// five times. Answers are kept in <paramref name="store"/> by file version. A bad file is a <see cref="ThumbnailStatus.Failed"/>,
/// never an exception.
/// </summary>
public sealed class MediaSignatureMaker(IProcessRunner runner, Func<FfmpegTools> tools, IMediaSignatureStore store, string workFolder)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The ffmpeg arguments; public so the exact command is testable. <paramref name="seeks"/> is empty for a picture.</summary>
    public static IReadOnlyList<string> Arguments(string path, IReadOnlyList<string> outputs, IReadOnlyList<TimeSpan> seeks, bool isNetwork)
    {
        List<string> arguments = ["-hide_banner", "-v", "error", "-y"];
        var inputs = Math.Max(1, seeks.Count);
        for (var i = 0; i < inputs; i++)
        {
            if (isNetwork)
            {
                arguments.AddRange(["-probesize", "4194304", "-analyzeduration", "3000000"]);
            }

            if (seeks.Count > 0)
            {
                arguments.AddRange(["-ss", seeks[i].TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
            }

            arguments.AddRange(["-i", "file:" + path]);
        }

        for (var i = 0; i < inputs; i++)
        {
            // area averaging keeps a re-sized copy close to the original; format=gray makes the output exactly 72 bytes.
            arguments.AddRange(["-map", $"{i}:v:0", "-frames:v", "1", "-an", "-sn", "-dn",
                "-vf", string.Create(CultureInfo.InvariantCulture, $"scale={DifferenceHash.Columns}:{DifferenceHash.Rows}:flags=area,format=gray"),
                "-f", "rawvideo", outputs[i]]);
        }

        return arguments;
    }

    /// <summary>The moments a video of <paramref name="duration"/> is sampled at: the fixed fractions, never in the last half second.</summary>
    public static IReadOnlyList<TimeSpan> SeeksFor(TimeSpan duration)
    {
        var latest = Math.Max(0, duration.TotalSeconds - 0.5);
        return [.. MediaSignature.VideoFrameFractions.Select(f => TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds * f, latest)))];
    }

    /// <summary>The signature of a picture or a video: from the store, or made now.</summary>
    /// <param name="key">The file version.</param>
    /// <param name="isVideo">True to sample frames across the file, false for a picture.</param>
    /// <param name="isNetwork">True for a file on a network drive.</param>
    /// <param name="duration">The video's length (required for a video: without it there is no way to space the frames).</param>
    /// <param name="cancellationToken">Cancels (and stops ffmpeg).</param>
    public async Task<SignatureResult> MakeAsync(MediaFileKey key, bool isVideo, bool isNetwork, TimeSpan? duration, CancellationToken cancellationToken)
    {
        if (store.Get(key) is { } cached && (cached.Kind == SignatureKind.Video) == isVideo)
        {
            return new SignatureResult(ThumbnailStatus.Ok, cached, null);
        }

        if (isVideo && duration is not { } length)
        {
            return new SignatureResult(ThumbnailStatus.Failed, null, "The length of the video is not known, so its frames cannot be spaced out.");
        }

        if (tools().FfmpegPath is not { } ffmpeg)
        {
            return new SignatureResult(ThumbnailStatus.ToolMissing, null, "ffmpeg was not found. Set the ffmpeg folder in Settings.");
        }

        var seeks = isVideo ? SeeksFor(duration!.Value) : [];
        Directory.CreateDirectory(workFolder);
        var run = Guid.NewGuid().ToString("N");
        var outputs = Enumerable.Range(0, Math.Max(1, seeks.Count)).Select(i => Path.Combine(workFolder, $"{run}-{i}.raw")).ToList();
        try
        {
            ProcessOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(ffmpeg, Arguments(key.Path, outputs, seeks, isNetwork), Timeout, 64 * 1024, cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                return new SignatureResult(ThumbnailStatus.ToolMissing, null, $"ffmpeg could not be started: {ex.Message}");
            }

            if (outcome.TimedOut)
            {
                return new SignatureResult(ThumbnailStatus.Failed, null, $"ffmpeg did not answer within {Timeout.TotalSeconds:0} s.");
            }

            var hashes = new List<ulong>(outputs.Count);
            foreach (var output in outputs)
            {
                var bytes = ReadFrame(output);
                if (bytes is null)
                {
                    var why = outcome.Error.Trim();
                    return new SignatureResult(ThumbnailStatus.Failed, null, string.IsNullOrEmpty(why) ? "No frame could be read." : why);
                }

                hashes.Add(DifferenceHash.FromGrey(bytes));
            }

            var signature = new MediaSignature(isVideo ? SignatureKind.Video : SignatureKind.Image, hashes, isVideo ? duration!.Value.TotalSeconds : 0);
            store.Put(key, signature);
            return new SignatureResult(ThumbnailStatus.Ok, signature, null);
        }
        finally
        {
            foreach (var output in outputs)
            {
                try
                {
                    File.Delete(output);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A leftover raw frame is 72 bytes in a work folder.
                }
            }
        }
    }

    private static byte[]? ReadFrame(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            return bytes.Length == DifferenceHash.PixelCount ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Makes signatures one at a time (spec §3.1): a duplicate search asks for thousands, each a process reading a file, and a disk or a
/// share is not helped by parallel reads. A request still waiting when its caller leaves never starts.
/// </summary>
public sealed class MediaSignatureService(MediaSignatureMaker maker) : IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private long _made;
    private long _failed;

    /// <inheritdoc />
    public void Dispose() => _slot.Dispose();

    /// <summary>Signatures made since the process started.</summary>
    public long Made => Interlocked.Read(ref _made);

    /// <summary>Files no signature could be made of since the process started.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Makes or fetches one signature; waits for its turn unless <paramref name="cancellationToken"/> is cancelled first.</summary>
    public async Task<SignatureResult> GetAsync(MediaFileKey key, bool isVideo, bool isNetwork, TimeSpan? duration, CancellationToken cancellationToken)
    {
        await _slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await maker.MakeAsync(key, isVideo, isNetwork, duration, cancellationToken).ConfigureAwait(false);
            if (result.Status == ThumbnailStatus.Ok)
            {
                Interlocked.Increment(ref _made);
            }
            else
            {
                Interlocked.Increment(ref _failed);
            }

            return result;
        }
        finally
        {
            _slot.Release();
        }
    }
}
