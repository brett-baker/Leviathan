using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.App;

/// <summary>
/// The learning's source of video facts: what the media process already has cached for the file (which needs no disk access, so it
/// works even after the file was moved or deleted), else a probe while the file still exists. Any failure is "no facts", never an error.
/// </summary>
internal sealed class TriageVideoFacts(IMediaInfoSource media) : IVideoFactsSource
{
    /// <inheritdoc />
    public async Task<VideoFacts?> GetAsync(string path, long size, long modifiedTicks, CancellationToken cancellationToken)
    {
        var file = new MediaFileRef(path, size, modifiedTicks, path.StartsWith(@"\\", StringComparison.Ordinal));
        try
        {
            var cached = await media.PeekAsync([file], cancellationToken).ConfigureAwait(false);
            var result = cached.TryGetValue(path, out var hit) ? hit : null;
            if (result is null && File.Exists(path))
            {
                result = await media.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return result is { State: MediaInfoState.Ok, Info: { } info } ? From(info) : null;
        }
        catch (Exception ex) when (ex is MediaInfoUnavailableException or MediaInfoTransientException)
        {
            return null;
        }
    }

    private static VideoFacts From(Leviathan.Media.MediaInfo info)
    {
        var video = info.PrimaryVideo;
        return new VideoFacts(info.Duration?.TotalSeconds, video?.Width, video?.Height, video?.Codec, info.BitRate ?? video?.BitRate);
    }
}
