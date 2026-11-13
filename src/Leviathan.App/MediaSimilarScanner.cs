using Leviathan.Contracts;
using Leviathan.Core.Duplicates;
using Leviathan.Engine.Supervision;
using Leviathan.Media;
using Leviathan.ViewModels.Duplicates;

namespace Leviathan.App;

/// <summary>
/// Finds look-alike pictures and videos for the results window (spec §15.2). The folder walk runs on the thread pool; every signature
/// comes from the media process (cached by file version there), so a crash or a hang in ffmpeg costs a restart of that process and a
/// note in the window, never the window. Once the groups are known, only their members are probed for width and height so the
/// "highest resolution" rule has something to compare.
/// </summary>
internal sealed class MediaSimilarScanner(Func<MediaHostConnection> host) : ISimilarMediaScanner
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);

    /// <inheritdoc />
    public async Task<DuplicateScan> ScanAsync(IReadOnlyList<string> roots, bool includeVideos, int tolerance, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var candidates = await Task.Run(() => Enumerate(roots, includeVideos, cancellationToken), cancellationToken).ConfigureAwait(false);
        IMediaService service;
        try
        {
            service = await host().GetServiceAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaHostUnavailableException ex)
        {
            throw new IOException(ex.Message, ex);
        }

        var result = await SimilarMediaFinder.FindAsync(
            candidates,
            (candidate, token) => SignatureAsync(service, candidate, token),
            new SimilarityOptions(tolerance),
            progress,
            cancellationToken).ConfigureAwait(false);

        var byPath = candidates.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        var sets = new List<IReadOnlyList<ReviewFile>>();
        foreach (var group in result.Groups)
        {
            var files = new List<ReviewFile>(group.Paths.Count);
            foreach (var path in group.Paths)
            {
                files.Add(await DescribeAsync(service, byPath[path], cancellationToken).ConfigureAwait(false));
            }

            sets.Add(files);
        }

        var notes = new List<string>(result.Unreadable);
        if (candidates.Count == 0)
        {
            notes.Add("There are no pictures or videos in this folder.");
        }

        return new DuplicateScan(DuplicateReview.SimilarGroupsFrom(sets), notes);
    }

    private static async Task<(MediaSignature? Signature, string? Problem)> SignatureAsync(IMediaService service, SimilarCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await service.GetSignatureAsync(
                new MediaSignatureRequest(candidate.Path, candidate.Size, candidate.ModifiedUtcTicks, IsNetwork(candidate.Path), candidate.IsVideo),
                cancellationToken).ConfigureAwait(false);
            return answer.Status == MediaThumbnailStatus.Ok && answer.Hashes.Length > 0
                ? (new MediaSignature(answer.IsVideo ? SignatureKind.Video : SignatureKind.Image, answer.Hashes, answer.DurationSeconds), null)
                : (null, answer.Message);
        }
        catch (Exception ex) when (ex is StreamJsonRpc.ConnectionLostException or StreamJsonRpc.RemoteInvocationException or ObjectDisposedException or IOException)
        {
            return (null, "the media process stopped while reading it.");
        }
    }

    private static async Task<ReviewFile> DescribeAsync(IMediaService service, SimilarCandidate candidate, CancellationToken cancellationToken)
    {
        var width = 0;
        var height = 0;
        try
        {
            var probe = await service.ProbeAsync(new MediaProbeRequest(candidate.Path, candidate.Size, candidate.ModifiedUtcTicks, IsNetwork(candidate.Path)), cancellationToken).ConfigureAwait(false);
            if (probe is { Status: MediaProbeStatus.Ok, Info.PrimaryVideo: { } video })
            {
                width = video.Width ?? 0;
                height = video.Height ?? 0;
            }
        }
        catch (Exception ex) when (ex is StreamJsonRpc.ConnectionLostException or StreamJsonRpc.RemoteInvocationException or ObjectDisposedException or IOException)
        {
            // No size in pixels: the rule falls back to the file size for this one.
        }

        return new ReviewFile(candidate.Path, candidate.Size, new DateTime(candidate.ModifiedUtcTicks, DateTimeKind.Utc), width, height);
    }

    private static List<SimilarCandidate> Enumerate(IReadOnlyList<string> roots, bool includeVideos, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        var found = new List<SimilarCandidate>();
        foreach (var root in roots)
        {
            try
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extension = file.Extension;
                    if (MediaFileKinds.IsImage(extension))
                    {
                        found.Add(new SimilarCandidate(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, IsVideo: false));
                    }
                    else if (includeVideos && MediaFileKinds.IsVideo(extension))
                    {
                        found.Add(new SimilarCandidate(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, IsVideo: true));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A root that cannot be read contributes nothing; the others go on.
            }
        }

        return found;
    }

    private static bool IsNetwork(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);
}
