using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Updates;
using Velopack;
using Velopack.Sources;

namespace Leviathan.App;

/// <summary>
/// The update feed: the GitHub Releases of the repository, read through Velopack (release plan §2, §4). It only asks what the newest
/// release is; nothing is downloaded or applied here. A copy that was not installed by the Velopack installer (a build from Visual Studio)
/// has no feed to compare with, so it answers "nothing newer". Every failure of the network or the feed is reported as an
/// <see cref="IOException"/>, which the checker treats as "offline", never as a crash.
/// </summary>
internal sealed class VelopackUpdateSource(string repositoryUrl) : Leviathan.ViewModels.Updates.IUpdateSource
{
    /// <inheritdoc />
    public async Task<UpdateOffer?> CheckAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Beta sees pre-releases as well as finished ones; stable sees finished ones only (the checker also refuses a beta for stable).
            var source = new GithubSource(repositoryUrl, accessToken: null, prerelease: request.Channel == ReleaseChannel.Beta);
            var manager = new UpdateManager(source);
            if (!manager.IsInstalled)
            {
                return null;
            }

            var found = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return found?.TargetFullRelease is { } release ? new UpdateOffer(release.Version.ToString(), release.NotesMarkdown) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"The update feed could not be read: {ex.Message}", ex);
        }
    }
}
