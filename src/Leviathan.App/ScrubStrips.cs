using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Contracts;
using Leviathan.Core.Model;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Leviathan.App;

/// <summary>A decoded hover-scrub strip: one picture of <see cref="Frames"/> frames side by side.</summary>
/// <param name="Image">The strip picture.</param>
/// <param name="Frames">Frames in it.</param>
/// <param name="CellWidth">Width of one frame in the picture.</param>
/// <param name="CellHeight">Height of one frame in the picture.</param>
/// <param name="Seconds">Where in the video each frame is from.</param>
internal sealed record ScrubStrip(BitmapImage Image, int Frames, int CellWidth, int CellHeight, double[] Seconds);

/// <summary>
/// The decoded strips of the videos the mouse has rested on (spec §10.5), the last few kept so going back to a video scrubs at once.
/// Used on the UI thread only: a strip is asked for once however many cells want it, and a video whose strip cannot be made is
/// remembered as such for the rest of the run so the mouse resting on it does not ask the media process again.
/// </summary>
internal sealed class ScrubStripCache(ScrubStripClient client)
{
    private const int Frames = 20;
    private const int Capacity = 24;

    private readonly Dictionary<string, Task<ScrubStrip?>> _strips = [];
    private readonly Queue<string> _order = new();

    /// <summary>The strip of <paramref name="video"/> for a picture <paramref name="edge"/> pixels across, or null when there is none.</summary>
    public Task<ScrubStrip?> GetAsync(FileEntry video, int edge, CancellationToken cancellationToken)
    {
        var cellWidth = edge > 256 ? 240 : 160;
        var key = $"{video.Location.ToWin32Path()}|{video.Size}|{video.Modified.UtcTicks}|{cellWidth}";
        if (_strips.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // Cancelling the cell that asked must not cancel the strip for a second cell that wants it, so the work is not tied to it.
        var task = LoadAsync(video, cellWidth);
        _strips[key] = task;
        _order.Enqueue(key);
        while (_order.Count > Capacity)
        {
            _strips.Remove(_order.Dequeue());
        }

        return task.WaitAsync(cancellationToken);
    }

    private async Task<ScrubStrip?> LoadAsync(FileEntry video, int cellWidth)
    {
        var answer = await client.GetAsync(video, Frames, cellWidth, CancellationToken.None);
        if (answer is not { Status: MediaThumbnailStatus.Ok, Jpeg: { Length: > 0 } jpeg } || answer.FrameSeconds.Length == 0)
        {
            return null;
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return new ScrubStrip(image, answer.Columns, answer.CellWidth, answer.CellHeight, answer.FrameSeconds);
    }
}
