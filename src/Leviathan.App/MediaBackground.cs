using Leviathan.Engine.Supervision;
using Leviathan.ViewModels.Hub;

namespace Leviathan.App;

/// <summary>
/// What the Background tab shows about the media process (spec §9.3): its counts and the files it gave up on. It asks the process
/// in the background every time the tab refreshes and keeps the last answer, so building the tab never waits on the process, and it
/// never starts the process just to ask (a process that exited when idle is shown as not running). "Retry" does start it.
/// </summary>
internal sealed class MediaBackground(Func<MediaHostConnection?> running, Func<MediaHostConnection> any)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private MediaBackgroundStats? _latest;
    private int _busy;

    /// <summary>The last answer, or null when the process is not running.</summary>
    public MediaBackgroundStats? Latest => Volatile.Read(ref _latest);

    /// <summary>Asks the process again, unless an earlier question is still out.</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (running() is not { IsConnected: true } host)
                {
                    Volatile.Write(ref _latest, null);
                    return;
                }

                using var timeout = new CancellationTokenSource(Limit);
                var service = await host.GetServiceAsync(Limit, timeout.Token).ConfigureAwait(false);
                var status = await service.GetStatusAsync(timeout.Token).ConfigureAwait(false);
                var unreadable = await service.GetUnreadableAsync(timeout.Token).ConfigureAwait(false);
                Volatile.Write(ref _latest, new MediaBackgroundStats(
                    status.Running, status.Waiting, status.Probed,
                    [.. unreadable.Select(a => new QuarantinedFile(a.Path, a.Message ?? "It could not be read."))]));
            }
            catch (Exception ex) when (IsConnectionProblem(ex))
            {
                // The process went away between the check and the question: shown as not running until it is back.
                Volatile.Write(ref _latest, null);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
    }

    /// <summary>Lets the process try <paramref name="path"/> again (starting it if it is not running).</summary>
    public async Task<bool> RetryAsync(string path)
    {
        try
        {
            var service = await any().GetServiceAsync(TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(false);
            var found = await service.RetryAsync(path, CancellationToken.None).ConfigureAwait(false);
            Refresh();
            return found;
        }
        catch (Exception ex) when (IsConnectionProblem(ex))
        {
            return false;
        }
    }

    private static bool IsConnectionProblem(Exception ex) =>
        ex is MediaHostUnavailableException or StreamJsonRpc.ConnectionLostException or StreamJsonRpc.RemoteInvocationException
            or ObjectDisposedException or IOException or OperationCanceledException or TimeoutException;
}
