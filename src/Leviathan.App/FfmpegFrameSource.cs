using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Contracts;
using Leviathan.Core.Platform;
using Leviathan.Engine.Supervision;
using Leviathan.ViewModels.Folders;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Leviathan.App;

/// <summary>
/// A frame of a video from the media process (spec §10.4), for files the shell has no thumbnail for. The process draws it with ffmpeg
/// into its disk cache and sends the small JPEG; this decodes it to pixels off the UI thread. Anything that goes wrong (the process is
/// off, ffmpeg is missing, the file is bad) is "no frame", and the view keeps the icon.
/// </summary>
internal sealed class FfmpegFrameSource(Func<MediaHostConnection> host) : IVideoFrameSource
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);

    /// <inheritdoc />
    public async Task<ThumbnailImage?> GetFrameAsync(ThumbnailRequest request, CancellationToken cancellationToken)
    {
        MediaThumbnailAnswer answer;
        try
        {
            var service = await host().GetServiceAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
            answer = await service.GetThumbnailAsync(
                new MediaThumbnailRequest(request.Location.ToWin32Path(), request.Length, request.Modified.UtcDateTime.Ticks, request.Location.IsNetwork, request.Size),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaHostUnavailableException or StreamJsonRpc.ConnectionLostException or StreamJsonRpc.RemoteInvocationException or IOException or ObjectDisposedException)
        {
            return null;
        }

        if (answer is not { Status: MediaThumbnailStatus.Ok, Jpeg: { Length: > 0 } jpeg })
        {
            return null;
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new ThumbnailImage((int)decoder.PixelWidth, (int)decoder.PixelHeight, pixels.DetachPixelData());
    }
}
