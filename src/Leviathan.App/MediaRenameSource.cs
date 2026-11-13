using System.Globalization;
using Leviathan.Contracts;
using Leviathan.Core.Rename;
using Leviathan.Engine.Supervision;
using Leviathan.Media;
using Leviathan.ViewModels.Rename;

namespace Leviathan.App;

/// <summary>
/// The facts behind the media tokens of the rename dialog (spec §15.3): length, resolution and codec from the media process's probe,
/// and the date taken from the file's recorded creation time when it has one. Files that are not media, or that cannot be read, are
/// left out, so a token that needs them makes just those items invalid.
/// </summary>
internal sealed class MediaRenameSource(Func<MediaHostConnection> host) : IRenameMediaSource
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, MediaFacts>> GetAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var wanted = paths.Where(p => MediaFileKinds.IsMedia(Path.GetExtension(p)) || MediaFileKinds.IsImage(Path.GetExtension(p))).ToList();
        var facts = new Dictionary<string, MediaFacts>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return facts;
        }

        IMediaService service;
        try
        {
            service = await host().GetServiceAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaHostUnavailableException)
        {
            return facts;
        }

        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (path, token) =>
        {
            try
            {
                var info = new FileInfo(path);
                var answer = await service.ProbeAsync(new MediaProbeRequest(path, info.Length, info.LastWriteTimeUtc.Ticks, path.StartsWith(@"\\", StringComparison.Ordinal)), token).ConfigureAwait(false);
                if (answer is { Status: MediaProbeStatus.Ok, Info: { } media })
                {
                    DateTime? taken = media.Tags.TryGetValue("creation_time", out var text)
                        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed.ToLocalTime()
                        : null;
                    var video = media.PrimaryVideo;
                    lock (facts)
                    {
                        facts[path] = new MediaFacts(taken, media.Duration, video?.Width, video?.Height, video?.Codec);
                    }
                }
            }
            catch (Exception ex) when (ex is StreamJsonRpc.ConnectionLostException or StreamJsonRpc.RemoteInvocationException or ObjectDisposedException or IOException or UnauthorizedAccessException)
            {
                // Left out: the items that need these facts are shown as invalid, with the reason.
            }
        }).ConfigureAwait(false);
        return facts;
    }
}
