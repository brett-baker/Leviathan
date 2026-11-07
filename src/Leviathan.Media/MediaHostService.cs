using System.Reflection;
using Leviathan.Contracts;
using Leviathan.Media;

namespace Leviathan.MediaHost;

/// <summary>
/// The <see cref="IMediaService"/> the UI calls (spec §9.3). A thin layer over <see cref="MediaMetadataService"/>: it turns the wire
/// requests into file keys and the cached or probed entries into answers, and nothing else, so what matters is tested there.
/// </summary>
public sealed class MediaHostService(MediaMetadataService metadata, FfmpegTools tools, int quarantinedAtStart, TimeProvider time, ThumbnailService thumbnails) : IMediaService
{
    private readonly DateTimeOffset _started = time.GetUtcNow();

    /// <inheritdoc />
    public Task<MediaHostInfo> PingAsync(CancellationToken cancellationToken) => Task.FromResult(new MediaHostInfo(
        ContractVersion.Major,
        ContractVersion.Minor,
        typeof(MediaHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
        Environment.ProcessId,
        _started,
        tools.FfprobePath,
        tools.Source,
        quarantinedAtStart));

    /// <inheritdoc />
    public async Task<MediaProbeAnswer> ProbeAsync(MediaProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = await metadata.GetAsync(KeyOf(request), request.IsNetwork, cancellationToken).ConfigureAwait(false);
        return Answer(entry);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaProbeAnswer>> PeekAsync(IReadOnlyList<MediaProbeRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var answers = new List<MediaProbeAnswer>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (metadata.Peek(KeyOf(request)) is { } entry)
            {
                answers.Add(Answer(entry));
            }
        }

        return Task.FromResult<IReadOnlyList<MediaProbeAnswer>>(answers);
    }

    /// <inheritdoc />
    public Task<MediaHostStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new MediaHostStatus(metadata.Running, metadata.Waiting, metadata.ProbedCount, metadata.UnreadableFiles().Count));

    /// <inheritdoc />
    public Task<bool> RetryAsync(string path, CancellationToken cancellationToken) => Task.FromResult(metadata.Retry(path));

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaProbeAnswer>> GetUnreadableAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaProbeAnswer>>([.. metadata.UnreadableFiles().Select(Answer)]);

    public async Task<MediaThumbnailAnswer> GetThumbnailAsync(MediaThumbnailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = new ThumbnailKey(request.Path, request.Size, request.ModifiedUtcTicks, Math.Clamp(request.Edge, 32, 1024));

        // The frame is taken a tenth of the way in, so the duration is wanted: from the cache when it is there, probed for a local file
        // (the probe is cached for the media columns too), and guessed for a network file rather than reading it twice.
        TimeSpan? duration = null;
        var file = KeyOf(new MediaProbeRequest(request.Path, request.Size, request.ModifiedUtcTicks, request.IsNetwork));
        if (metadata.Peek(file) is { Info: { } known })
        {
            duration = known.Duration;
        }
        else if (!request.IsNetwork && thumbnails.CachedFile(key) is null)
        {
            var probed = await metadata.GetAsync(file, request.IsNetwork, cancellationToken).ConfigureAwait(false);
            duration = probed.Info?.Duration;
        }

        var result = await thumbnails.GetAsync(key, request.IsNetwork, duration, cancellationToken).ConfigureAwait(false);
        if (result.Status != ThumbnailStatus.Ok)
        {
            return new MediaThumbnailAnswer(request.Path, result.Status == ThumbnailStatus.ToolMissing ? MediaThumbnailStatus.ToolMissing : MediaThumbnailStatus.Failed, null, result.Message);
        }

        try
        {
            return new MediaThumbnailAnswer(request.Path, MediaThumbnailStatus.Ok, await File.ReadAllBytesAsync(result.File!, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Trimmed from the cache between drawing and reading: asking again draws it again.
            return new MediaThumbnailAnswer(request.Path, MediaThumbnailStatus.Failed, null, ex.Message);
        }
    }

    internal static MediaFileKey KeyOf(MediaProbeRequest request) => new(request.Path, request.Size, new DateTime(request.ModifiedUtcTicks, DateTimeKind.Utc));

    internal static MediaProbeAnswer Answer(MediaMetadataEntry entry) => entry.State switch
    {
        MetadataState.Ok => new MediaProbeAnswer(entry.Key.Path, MediaProbeStatus.Ok, entry.Info, null),
        MetadataState.ToolMissing => new MediaProbeAnswer(entry.Key.Path, MediaProbeStatus.ToolMissing, null, entry.Message),
        _ => new MediaProbeAnswer(entry.Key.Path, MediaProbeStatus.Unreadable, null, entry.Message),
    };
}
