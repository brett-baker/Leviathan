using Leviathan.Contracts;
using Leviathan.Core.Model;
using Leviathan.Engine.Supervision;
using Leviathan.ViewModels.Hub;

namespace Leviathan.App;

/// <summary>
/// Asks the media process for a contact sheet (spec §10.4). It never throws for a process that is off or a file that is bad: the
/// answer says what went wrong, in words the window can show.
/// </summary>
internal sealed class ContactSheetClient(Func<MediaHostConnection> host)
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The sheet of <paramref name="video"/> as a grid of <paramref name="grid"/> × <paramref name="grid"/> frames.</summary>
    public async Task<MediaContactSheetAnswer> GetAsync(FileEntry video, int grid, int cellWidth, CancellationToken cancellationToken)
    {
        var request = new MediaContactSheetRequest(video.Location.ToWin32Path(), video.Size, video.Modified.UtcDateTime.Ticks, video.Location.IsNetwork, grid, grid, cellWidth);
        try
        {
            var service = await host().GetServiceAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
            return await service.GetContactSheetAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaHostUnavailableException or StreamJsonRpc.ConnectionLostException or StreamJsonRpc.RemoteInvocationException or IOException or ObjectDisposedException)
        {
            return new MediaContactSheetAnswer(request.Path, MediaThumbnailStatus.Failed, null, grid, grid, cellWidth, 0, [], ex is MediaHostUnavailableException ? ex.Message : "The media process stopped answering.");
        }
    }
}

/// <summary>
/// A folder of contact sheets made one after another in the background (spec §10.4): each video's sheet is saved as
/// "name - contact sheet.jpg" in the folder the user chose, never over a file that is already there. Progress is read by the
/// Background tab; Cancel stops after the video being worked on. One batch at a time.
/// </summary>
internal sealed class ContactSheetBatch(ContactSheetClient client, Action changed) : IDisposable
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private ContactSheetBatchStats? _stats;

    /// <summary>The batch as it is now, or null when none was started.</summary>
    public ContactSheetBatchStats? Stats
    {
        get
        {
            lock (_lock)
            {
                return _stats;
            }
        }
    }

    /// <summary>True while a batch runs.</summary>
    public bool IsRunning => Stats is { Running: true };

    /// <summary>Starts making the sheets; false when a batch is already running.</summary>
    public bool Start(IReadOnlyList<FileEntry> videos, string folder)
    {
        lock (_lock)
        {
            if (_stats is { Running: true })
            {
                return false;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _stats = new ContactSheetBatchStats(folder, 0, videos.Count, 0, true, null);
        }

        _ = Task.Run(() => RunAsync(videos, folder, _cts.Token));
        changed();
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Stops after the video being worked on.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }
    }

    private async Task RunAsync(IReadOnlyList<FileEntry> videos, string folder, CancellationToken cancellationToken)
    {
        var done = 0;
        var failed = 0;
        try
        {
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Update(done, videos.Count, failed, true, video.Name);
                var saved = false;
                try
                {
                    var answer = await client.GetAsync(video, 4, 240, cancellationToken).ConfigureAwait(false);
                    if (answer is { Status: MediaThumbnailStatus.Ok, Jpeg: { } jpeg })
                    {
                        await File.WriteAllBytesAsync(UniquePath(folder, video.Name), jpeg, cancellationToken).ConfigureAwait(false);
                        saved = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The folder is full or cannot be written to: this one counts as failed, the next may still work.
                }

                done++;
                if (!saved)
                {
                    failed++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled: what was made stays, and the tab says where it stopped.
        }
        finally
        {
            Update(done, videos.Count, failed, false, null);
        }
    }

    private void Update(int done, int total, int failed, bool running, string? current)
    {
        lock (_lock)
        {
            _stats = new ContactSheetBatchStats(_stats?.Folder ?? string.Empty, done, total, failed, running, current);
        }

        changed();
    }

    /// <summary>"name - contact sheet.jpg" in <paramref name="folder"/>, or "name - contact sheet (2).jpg" and so on when that exists.</summary>
    internal static string UniquePath(string folder, string videoName)
    {
        var stem = Path.GetFileNameWithoutExtension(videoName) + " - contact sheet";
        var path = Path.Combine(folder, stem + ".jpg");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}).jpg");
        }

        return path;
    }
}
