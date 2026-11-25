using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Updates;
using Velopack;
using Velopack.Sources;

namespace Leviathan.App;

/// <summary>
/// The update feed and installer: the GitHub Releases of the repository, read through Velopack (release plan §2, §4). A check only asks
/// what the newest release is; downloading and applying it are separate steps the user starts. A copy that was not installed by the
/// Velopack installer (a build from Visual Studio) has no feed to compare with, so it answers "nothing newer" and cannot be updated. Every
/// failure of the network or the feed is reported as an <see cref="IOException"/>, which the checker treats as "offline", never a crash.
/// </summary>
internal sealed class VelopackUpdateSource(string repositoryUrl) : Leviathan.ViewModels.Updates.IUpdateSource, IUpdateApplier
{
    private UpdateManager? _manager;
    private UpdateInfo? _found;

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
            (_manager, _found) = (manager, found);
            return found?.TargetFullRelease is { } release ? new UpdateOffer(release.Version.ToString(), release.NotesMarkdown) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"The update feed could not be read: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        if (_manager is not { } manager || _found is not { } found)
        {
            throw new InvalidOperationException("There is no update to download. Check for updates first.");
        }

        try
        {
            // Velopack checks the file against the hash in the feed and discards it when it does not match.
            await manager.DownloadUpdatesAsync(found, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or InvalidOperationException))
        {
            throw new IOException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public void ApplyAndRestart()
    {
        if (_manager is not { } manager || _found?.TargetFullRelease is not { } release)
        {
            throw new InvalidOperationException("There is no downloaded update to install.");
        }

        // Starts Velopack's Update.exe, which waits for this process to end, swaps the folder, keeps the old version for roll-back and starts the new one.
        manager.ApplyUpdatesAndRestart(release);
    }
}
