using System.Reflection;
using Leviathan.Contracts;
using Leviathan.Media;
using Microsoft.Extensions.Logging;

namespace Leviathan.MediaHost;

/// <summary>
/// The <see cref="IMediaService"/> the UI calls (spec §9.3). A thin layer over <see cref="MediaMetadataService"/>: it turns the wire
/// requests into file keys and the cached or probed entries into answers, and nothing else, so what matters is tested there.
/// </summary>
public sealed class MediaHostService(MediaMetadataService metadata, FfmpegTools tools, int quarantinedAtStart, TimeProvider time, ThumbnailService thumbnails, ContactSheetService sheets, Microsoft.Extensions.Logging.ILogger? logger = null) : IMediaService
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

        ThumbnailResult result;
        try
        {
            result = await thumbnails.GetAsync(key, request.IsNetwork, duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The row scrolled away or the view changed: not a failure, and the caller is gone.
            logger?.LogDebug("Thumbnail of {Path} was cancelled", request.Path);
            throw;
        }

        if (result.Status != ThumbnailStatus.Ok)
        {
            logger?.LogWarning("Thumbnail of {Path} failed ({Status}) using {Ffmpeg}: {Message}", request.Path, result.Status, tools.FfmpegPath ?? "no ffmpeg", result.Message);
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

    public Task<MediaContactSheetAnswer> GetContactSheetAsync(MediaContactSheetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SheetAsync(request.Path, request.Size, request.ModifiedUtcTicks, request.IsNetwork,
            (width, height) => ContactSheetSpec.For(request.Columns, request.Rows, request.CellWidth, width, height), cancellationToken);
    }

    public Task<MediaContactSheetAnswer> GetScrubStripAsync(MediaScrubRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SheetAsync(request.Path, request.Size, request.ModifiedUtcTicks, request.IsNetwork,
            (width, height) => ContactSheetSpec.Strip(request.Frames, request.CellWidth, width, height), cancellationToken);
    }

    private async Task<MediaContactSheetAnswer> SheetAsync(string path, long size, long modifiedUtcTicks, bool isNetwork, Func<int?, int?, ContactSheetSpec> shape, CancellationToken cancellationToken)
    {
        // The frames are spaced across the whole video and sized like it, so the probe (cached) is needed first.
        var file = KeyOf(new MediaProbeRequest(path, size, modifiedUtcTicks, isNetwork));
        var probed = metadata.Peek(file) ?? await metadata.GetAsync(file, isNetwork, cancellationToken).ConfigureAwait(false);
        var video = probed.Info?.PrimaryVideo;
        var spec = shape(video?.Width, video?.Height);
        if (probed.Info is null || video is null)
        {
            return Failed(path, spec, probed.State == MetadataState.ToolMissing ? MediaThumbnailStatus.ToolMissing : MediaThumbnailStatus.Failed, probed.Message ?? "There is no video in this file.");
        }

        ContactSheetResult result;
        try
        {
            result = await sheets.GetAsync(path, size, modifiedUtcTicks, isNetwork, probed.Info.Duration, spec, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger?.LogDebug("Contact sheet of {Path} was cancelled", path);
            throw;
        }

        if (result.Status != ThumbnailStatus.Ok)
        {
            logger?.LogWarning("Contact sheet of {Path} failed ({Status}) using {Ffmpeg}: {Message}", path, result.Status, tools.FfmpegPath ?? "no ffmpeg", result.Message);
            return Failed(path, spec, result.Status == ThumbnailStatus.ToolMissing ? MediaThumbnailStatus.ToolMissing : MediaThumbnailStatus.Failed, result.Message);
        }

        try
        {
            return new MediaContactSheetAnswer(
                path, MediaThumbnailStatus.Ok, await File.ReadAllBytesAsync(result.File!, cancellationToken).ConfigureAwait(false),
                spec.Columns, spec.Rows, spec.CellWidth, spec.CellHeight, [.. result.Times.Select(t => t.TotalSeconds)], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(path, spec, MediaThumbnailStatus.Failed, ex.Message);
        }
    }

    private static MediaContactSheetAnswer Failed(string path, ContactSheetSpec spec, MediaThumbnailStatus status, string? message) =>
        new(path, status, null, spec.Columns, spec.Rows, spec.CellWidth, spec.CellHeight, [], message);

    internal static MediaFileKey KeyOf(MediaProbeRequest request) => new(request.Path, request.Size, new DateTime(request.ModifiedUtcTicks, DateTimeKind.Utc));

    internal static MediaProbeAnswer Answer(MediaMetadataEntry entry) => entry.State switch
    {
        MetadataState.Ok => new MediaProbeAnswer(entry.Key.Path, MediaProbeStatus.Ok, entry.Info, null),
        MetadataState.ToolMissing => new MediaProbeAnswer(entry.Key.Path, MediaProbeStatus.ToolMissing, null, entry.Message),
        _ => new MediaProbeAnswer(entry.Key.Path, MediaProbeStatus.Unreadable, null, entry.Message),
    };
}
