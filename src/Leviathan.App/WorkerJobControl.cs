using Leviathan.Contracts;
using Leviathan.ViewModels.Hub;

namespace Leviathan.App;

/// <summary>
/// The Operations tab's job controls over the worker connection (spec §12.6). A worker that is not running has no jobs, so the
/// calls do nothing; one that went away mid-call is not an error the user can act on, the next snapshot shows what is true.
/// </summary>
internal sealed class WorkerJobControl(Func<WorkerConnection?> worker) : IJobControl
{
    public Task PauseAsync(Guid jobId) => Call(w => w.PauseAsync(jobId));

    public Task ResumeAsync(Guid jobId) => Call(w => w.ResumeAsync(jobId));

    public Task CancelAsync(Guid jobId) => Call(w => w.CancelAsync(jobId));

    public Task RemoveAsync(Guid jobId) => Call(w => w.RemoveAsync(jobId));

    public Task RunNowAsync(Guid jobId) => Call(w => w.RunNowAsync(jobId));

    public Task MoveAsync(Guid jobId, int queuePosition) => Call(w => w.MoveAsync(jobId, queuePosition));

    public Task SetPriorityAsync(Guid jobId, JobPriority priority) => Call(w => w.SetPriorityAsync(jobId, priority));

    public Task PauseAllAsync() => Call(w => w.PauseAllAsync());

    public Task ResumeAllAsync() => Call(w => w.ResumeAllAsync());

    private async Task Call(Func<WorkerConnection, Task> action)
    {
        if (worker() is not { } connection)
        {
            return;
        }

        try
        {
            await Task.Run(() => action(connection)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException)
        {
            // The worker is gone or the job just ended: there is nothing left to do to it.
        }
    }
}
