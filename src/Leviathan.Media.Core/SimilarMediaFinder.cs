namespace Leviathan.Media;

/// <summary>A picture or video to look at: where it is and which version of it (the cache identity).</summary>
/// <param name="Path">Win32 path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ModifiedUtcTicks">Last write time in UTC ticks.</param>
/// <param name="IsVideo">True for a video, false for a picture.</param>
public sealed record SimilarCandidate(string Path, long Size, long ModifiedUtcTicks, bool IsVideo);

/// <summary>What a similar-media search found.</summary>
/// <param name="Groups">Look-alike groups, the largest first.</param>
/// <param name="Unreadable">One sentence for each file that has no signature (ffmpeg could not read it), so the person is told rather than left to wonder.</param>
/// <param name="Examined">Files whose signature was obtained.</param>
public sealed record SimilarMediaResult(IReadOnlyList<SimilarGroup> Groups, IReadOnlyList<string> Unreadable, int Examined);

/// <summary>
/// Runs the similar-media search (spec §15.2): asks for the signature of every candidate, a few at a time, and groups the ones that
/// look alike. Signatures come from a delegate (the media process in the app, a fake in tests), so this holds the orchestration only:
/// bounded parallelism, cancellation, progress, and a file with no signature being reported instead of stopping the search.
/// </summary>
public static class SimilarMediaFinder
{
    /// <summary>How many signatures are asked for at once; the media process draws them one at a time, so more only keeps its queue full.</summary>
    public const int Parallelism = 4;

    /// <summary>
    /// Finds look-alikes among <paramref name="candidates"/>. <paramref name="signatureOf"/> returns the signature, or null with a reason
    /// in the second element when there is none. <paramref name="progress"/> hears the file being looked at.
    /// </summary>
    public static async Task<SimilarMediaResult> FindAsync(
        IReadOnlyList<SimilarCandidate> candidates,
        Func<SimilarCandidate, CancellationToken, Task<(MediaSignature? Signature, string? Problem)>> signatureOf,
        SimilarityOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(signatureOf);
        ArgumentNullException.ThrowIfNull(options);
        var found = new (string Path, MediaSignature Signature)?[candidates.Count];
        var unreadable = new string?[candidates.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var candidate = candidates[index];
                progress?.Report(candidate.Path);
                var (signature, problem) = await signatureOf(candidate, token).ConfigureAwait(false);
                if (signature is { Hashes.Count: > 0 })
                {
                    found[index] = (candidate.Path, signature);
                }
                else
                {
                    unreadable[index] = $"'{System.IO.Path.GetFileName(candidate.Path)}': {problem ?? "no picture could be read from it."}";
                }
            }).ConfigureAwait(false);

        var signed = found.Where(f => f is not null).Select(f => f!.Value).ToList();
        var groups = await Task.Run(() => SimilarMediaMatcher.Group(signed, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new SimilarMediaResult(groups, [.. unreadable.Where(u => u is not null).Select(u => u!)], signed.Count);
    }
}
