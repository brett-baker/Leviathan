using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Contracts;
using Leviathan.Core.Model;
using Leviathan.Core.Platform;
using Leviathan.Engine.Supervision;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Leviathan.App;

/// <summary>
/// Asks the media process for the waveform or the embedded cover art of an audio file (spec §9.1) and decodes the answer to a picture.
/// Like the other media clients it never throws for a media process that is off or a file with no picture: that is "no picture", and the
/// pane simply shows the file name as before. Must be called on the UI thread, because it makes the bitmaps.
/// </summary>
internal sealed class AudioArtClient(Func<MediaHostConnection> host)
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(4);

    /// <summary>The picture, or null when there is none.</summary>
    public async Task<BitmapImage?> GetAsync(FileEntry audio, bool cover, int width, int height, CancellationToken cancellationToken)
    {
        MediaThumbnailAnswer answer;
        try
        {
            var service = await host().GetServiceAsync(StartTimeout, cancellationToken);
            answer = await service.GetAudioArtAsync(
                new MediaAudioArtRequest(audio.Location.ToWin32Path(), audio.Size, audio.Modified.UtcDateTime.Ticks, audio.Location.IsNetwork, cover, width, height),
                cancellationToken);
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
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }
}
