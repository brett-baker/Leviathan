using System.Collections.Concurrent;
using Leviathan.Contracts;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.App;

/// <summary>A copy or move job the worker finished (or gave up on), with the request that started it.</summary>
/// <param name="Request">What was asked.</param>
/// <param name="Job">The worker's last word on the job.</param>
internal sealed record TransferFinished(TransferRequest Request, JobSnapshot Job);

/// <summary>
/// Hands drops and pastes to the worker as copy and move jobs (spec §12.1, §12.3). The UI only submits and listens: the copying runs
/// in <c>Leviathan.Worker</c>, so closing a window or crashing the UI never interrupts it.
/// </summary>
internal sealed class WorkerTransfers : ITransferService
{
    private readonly Func<WorkerConnection> _worker;
    private readonly ConcurrentDictionary<Guid, TransferRequest> _jobs = new();
    private readonly ConcurrentDictionary<Guid, JobSnapshot> _early = new();
    private WorkerConnection? _connection;

    /// <summary>Creates the service; <paramref name="worker"/> is called on first use.</summary>
    public WorkerTransfers(Func<WorkerConnection> worker) => _worker = worker;

    /// <summary>Raised on a thread-pool thread when a job ends.</summary>
    public event EventHandler<TransferFinished>? Finished;

    /// <inheritdoc />
    public async Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode == TransferMode.Link)
        {
            return new TransferOutcome(false, "Creating shortcuts arrives with a later update. Nothing was changed.");
        }

        try
        {
            var job = new JobRequest(request.Mode == TransferMode.Move ? JobKind.Move : JobKind.Copy, [.. request.Sources.Select(s => s.Value)], request.Destination.Value);
            var snapshot = await Task.Run(() => Connection().SubmitAsync(job), cancellationToken).ConfigureAwait(true);
            _jobs[snapshot.Id] = request;
            if (_early.TryRemove(snapshot.Id, out var early))
            {
                Handle(early);
            }

            var verb = request.Mode == TransferMode.Move ? "Moving" : "Copying";
            var what = request.Sources.Count == 1 ? request.Sources[0].Name : $"{request.Sources.Count} items";
            return new TransferOutcome(true, $"{verb} {what} to {request.Destination.Name}…");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TransferOutcome(false, $"Could not start the transfer: {ex.Message}");
        }
    }

    private WorkerConnection Connection()
    {
        if (_connection is null)
        {
            _connection = _worker();
            _connection.JobChanged += OnJobChanged;
        }

        return _connection;
    }

    private void OnJobChanged(object? sender, JobSnapshot snapshot)
    {
        if (snapshot.State is not (JobState.Completed or JobState.Failed or JobState.Cancelled) || snapshot.Kind is not (JobKind.Copy or JobKind.Move))
        {
            return;
        }

        if (_jobs.ContainsKey(snapshot.Id))
        {
            Handle(snapshot);
        }
        else
        {
            // The worker can report the end before our own submit call returned.
            _early[snapshot.Id] = snapshot;
        }
    }

    private void Handle(JobSnapshot snapshot)
    {
        if (_jobs.TryRemove(snapshot.Id, out var request))
        {
            Finished?.Invoke(this, new TransferFinished(request, snapshot));
        }
    }
}
