using Leviathan.Core.Platform;
using Leviathan.Media;

namespace Leviathan.ViewModels.Folders;

/// <summary>Draws a frame of a video itself, for the files the shell has no thumbnail for (spec §10.4).</summary>
public interface IVideoFrameSource
{
    /// <summary>The frame as pixels, or null when none could be drawn. Cancelling stops the work.</summary>
    Task<ThumbnailImage?> GetFrameAsync(ThumbnailRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Thumbnails for video (spec §10.4): the shell's own when it has one (a thumbnail handler, or one Windows already cached), otherwise
/// a frame drawn by ffmpeg in the media process, otherwise the file-type icon as before. Everything that is not a video, and every
/// request that is not for a thumbnail (icons only, cache only), goes straight to the shell.
/// </summary>
public sealed class VideoThumbnailSource(IThumbnailSource shell, IVideoFrameSource frames, bool preferFrames = false) : IThumbnailSource
{
    /// <inheritdoc />
    public async Task<ThumbnailImage?> CreateAsync(ThumbnailRequest request, CancellationToken cancellationToken)
    {
        if (request.Mode != ThumbnailMode.Thumbnail || !IsVideo(request))
        {
            return await shell.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (!preferFrames && await shell.CreateAsync(request with { Mode = ThumbnailMode.ThumbnailOnly }, cancellationToken).ConfigureAwait(false) is { } real)
        {
            return real;
        }

        try
        {
            if (await frames.GetFrameAsync(request, cancellationToken).ConfigureAwait(false) is { } frame)
            {
                return frame;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The media process is off, ffmpeg is missing or the file is bad: the icon is the answer, as it always was.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await shell.CreateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsVideo(ThumbnailRequest request) =>
        request.Location.Name is { Length: > 0 } name && MediaFileKinds.IsVideo(Path.GetExtension(name));
}
