using Leviathan.Contracts;
using Leviathan.Engine.Supervision;
using Leviathan.ViewModels.Folders;

namespace Leviathan.App;

/// <summary>
/// The media columns' source (spec §9.3): the media process, started the first time a folder with media information is shown, behind
/// <see cref="IMediaInfoSource"/>. It only translates: a process that cannot be used becomes "unavailable" (the tracker stops asking
/// for this folder), a connection that dropped becomes "transient" (the tracker asks again, which starts the process again), and
/// ffprobe not being installed is "unavailable" too, so a folder of a thousand videos does not cause a thousand failed probes.
/// </summary>
internal sealed class MediaInfoSource(Func<MediaHostConnection> host) : IMediaInfoSource
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, MediaInfoResult>> PeekAsync(IReadOnlyList<MediaFileRef> files, CancellationToken cancellationToken)
    {
        var service = await ServiceAsync(cancellationToken).ConfigureAwait(false);
        var answers = await Translate(() => service.PeekAsync([.. files.Select(Request)], cancellationToken)).ConfigureAwait(false);
        return answers.Where(a => a.Status != MediaProbeStatus.ToolMissing).ToDictionary(a => a.Path, Result, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<MediaInfoResult> ProbeAsync(MediaFileRef file, CancellationToken cancellationToken)
    {
        var service = await ServiceAsync(cancellationToken).ConfigureAwait(false);
        var answer = await Translate(() => service.ProbeAsync(Request(file), cancellationToken)).ConfigureAwait(false);
        return answer.Status == MediaProbeStatus.ToolMissing
            ? throw new MediaInfoUnavailableException(answer.Message ?? "ffprobe was not found.")
            : Result(answer);
    }

    private async Task<IMediaService> ServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await host().GetServiceAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaHostUnavailableException ex)
        {
            throw new MediaInfoUnavailableException(ex.Message, ex);
        }
    }

    private static async Task<T> Translate<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StreamJsonRpc.ConnectionLostException or StreamJsonRpc.RemoteInvocationException or ObjectDisposedException or IOException)
        {
            throw new MediaInfoTransientException("The media process stopped answering.", ex);
        }
    }

    private static MediaProbeRequest Request(MediaFileRef file) => new(file.Path, file.Size, file.ModifiedUtcTicks, file.IsNetwork);

    private static MediaInfoResult Result(MediaProbeAnswer answer) => answer.Status == MediaProbeStatus.Ok
        ? new MediaInfoResult(MediaInfoState.Ok, answer.Info, null)
        : new MediaInfoResult(MediaInfoState.Unreadable, null, answer.Message);
}
