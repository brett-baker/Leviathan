using System.Collections.Concurrent;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.App;

/// <summary>A copy, move or delete job the worker finished (or gave up on), with what it was asked to do.</summary>
/// <param name="Kind">Copy, Move or Delete.</param>
/// <param name="Sources">The items it worked on.</param>
/// <param name="Destination">The folder they went to; null for a delete.</param>
/// <param name="Permanent">True for a permanent delete.</param>
/// <param name="Job">The worker's last word on the job.</param>
internal sealed record TransferFinished(JobKind Kind, IReadOnlyList<Leviathan.Core.Paths.LocationPath> Sources, Leviathan.Core.Paths.LocationPath? Destination, bool Permanent, JobSnapshot Job)
{
    /// <summary>How the job ended, in words: "Copied 3 items to media", "Cancelled: a.mkv", or the worker's reason for a failure.</summary>
    public string Describe()
    {
        var what = Sources.Count == 1 ? Sources[0].Name : $"{Sources.Count} items";
        var done = Kind switch
        {
            JobKind.Move => $"Moved {what} to {Destination?.Name}",
            JobKind.Delete when Permanent => $"Deleted {what}",
            JobKind.Delete => $"Moved {what} to the Recycle Bin",
            _ => $"Copied {what} to {Destination?.Name}",
        };
        return Job.State switch
        {
            JobState.Completed => done,
            JobState.Cancelled => $"Cancelled: {what}",
            _ => Job.Error ?? $"Could not finish: {what}",
        };
    }
}

/// <summary>
/// Hands drops and pastes to the worker as copy and move jobs (spec §12.1, §12.3). The UI only submits and listens: the copying runs
/// in <c>Leviathan.Worker</c>, so closing a window or crashing the UI never interrupts it.
/// </summary>
internal sealed class WorkerTransfers : ITransferService, IDeleteService
{
    private readonly Func<WorkerConnection> _worker;
    private readonly ConcurrentDictionary<Guid, Pending> _jobs = new();
    private readonly ConcurrentDictionary<Guid, JobSnapshot> _early = new();
    private WorkerConnection? _connection;

    /// <summary>Creates the service; <paramref name="worker"/> is called on first use.</summary>
    public WorkerTransfers(Func<WorkerConnection> worker)
    {
        _worker = worker;
    }

    /// <summary>Where the verify policy of the settings is read from (set once the settings exist); copies are checked the default way until then.</summary>
    public Func<VerifyMode> VerifySource { get; set; } = () => VerifyMode.Default;

    /// <summary>Tells what kind of drive a letter is, for the Delete table of spec §12.5; every drive is taken to have a Recycle Bin until it is set.</summary>
    public IVolumeInfo? Volumes { get; set; }

    /// <inheritdoc />
    public async Task<DeleteBehavior> BehaviorAsync(IReadOnlyList<LocationPath> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (Volumes is not { } volumes)
        {
            return DeleteBehavior.RecycleBin;
        }

        // One question per drive letter, not per item, and none for a share: a UNC path is always a soft delete.
        var behaviors = new List<DeleteBehavior>();
        foreach (var group in items.GroupBy(i => i.Kind == LocationKind.Drive ? char.ToUpperInvariant(i.Root[0]).ToString() : i.Root, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var drive = first.Kind == LocationKind.Drive ? await volumes.GetDriveAsync(first, cancellationToken).ConfigureAwait(false) : null;
            behaviors.Add(DeletePolicy.Decide(first, drive));
        }

        return DeletePolicy.Strictest(behaviors);
    }

    /// <summary>Where the "keep Leviathan's own trash on shares" setting is read from; on until the settings exist.</summary>
    public Func<bool> KeepOwnTrashSource { get; set; } = () => true;

    /// <summary>Raised on a thread-pool thread when a job ends.</summary>
    public event EventHandler<TransferFinished>? Finished;

    /// <summary>Raised on a thread-pool thread while a delete runs, with the items it has removed since the last time.</summary>
    public event EventHandler<IReadOnlyList<LocationPath>>? ItemsDeleted;

    /// <summary>Raised on a thread-pool thread when a delete to the Recycle Bin or a share's recycle folder ended, with what went where.</summary>
    public event EventHandler<IReadOnlyList<ViewModels.Undo.ItemMove>>? DeleteCompleted;

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
            var job = new JobRequest(request.Mode == TransferMode.Move ? JobKind.Move : JobKind.Copy, [.. request.Sources.Select(s => s.Value)], request.Destination.Value)
            {
                // A permanent error stops the job on the Skip / Retry / Skip all like this question, which the Operations tab shows
                // (spec §12.2); the strip's red tip says a job waits for an answer.
                OnError = ErrorMode.Ask,
                Verify = VerifySource(),

                // Name clashes are asked once for the whole job, with the list of files (spec §12.4). The job never overwrites on its own.
                Conflict = ConflictPolicy.Ask,
            };
            var snapshot = await Task.Run(() => Connection().SubmitAsync(job), cancellationToken).ConfigureAwait(true);
            _jobs[snapshot.Id] = new Pending(job.Kind, request.Sources, request.Destination, false);
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

    /// <inheritdoc />
    public async Task<TransferOutcome> DeleteAsync(IReadOnlyList<LocationPath> items, bool permanent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        try
        {
            var job = new JobRequest(JobKind.Delete, [.. items.Select(i => i.Value)], null) { DeleteMode = permanent ? DeleteMode.Permanent : DeleteMode.RecycleBin, KeepOwnTrash = KeepOwnTrashSource() };
            var snapshot = await Task.Run(() => Connection().SubmitAsync(job), cancellationToken).ConfigureAwait(true);
            _jobs[snapshot.Id] = new Pending(JobKind.Delete, items, null, permanent);
            if (_early.TryRemove(snapshot.Id, out var early))
            {
                Handle(early);
            }

            var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
            return new TransferOutcome(true, permanent ? $"Deleting {what}…" : $"Moving {what} to the Recycle Bin…");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TransferOutcome(false, $"Could not start the delete: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a finished job again for the items that failed ("Retry failed", spec §12.3): a copy or move to the same destination, or a
    /// delete (to the Recycle Bin, whatever the first one did, since it is the one that can be undone). Says so when the report
    /// holds nothing to retry or no destination.
    /// </summary>
    public async Task<TransferOutcome> RetryFailedAsync(ViewModels.Hub.OperationReportView report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var failed = report.FailedSources.Select(s => LocationPath.TryParse(s, out var p) ? p : null).OfType<LocationPath>().ToList();
        if (failed.Count == 0 || !report.CanRetryFailed)
        {
            return new TransferOutcome(false, "There is nothing to retry.");
        }

        if (report.Report.Kind == JobKind.Delete)
        {
            return await DeleteAsync(failed, permanent: false, cancellationToken).ConfigureAwait(true);
        }

        if (!LocationPath.TryParse(report.Destination, out var destination))
        {
            return new TransferOutcome(false, "The destination is not known any more.");
        }

        var mode = report.Report.Kind == JobKind.Move ? TransferMode.Move : TransferMode.Copy;
        return await StartAsync(new TransferRequest(failed, destination, mode), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Cancels a queued or running job; a copy keeps what it finished, and a delete stops before the next item.</summary>
    public async Task CancelAsync(Guid jobId)
    {
        try
        {
            await Task.Run(() => Connection().CancelAsync(jobId)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException)
        {
            // The worker is gone or the job just ended: there is nothing left to cancel.
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
        AnnounceDeleted(snapshot);
        if (snapshot.State is not (JobState.Completed or JobState.Failed or JobState.Cancelled) || snapshot.Kind is not (JobKind.Copy or JobKind.Move or JobKind.Delete))
        {
            return;
        }

        if (_jobs.ContainsKey(snapshot.Id))
        {
            Handle(snapshot);
        }
        else
        {
            // The worker can report the end before our own submit call returned. Jobs that are not ours (undo's) would pile up here.
            if (_early.Count > 32)
            {
                _early.Clear();
            }

            _early[snapshot.Id] = snapshot;
        }
    }

    // A delete works through its items in the order given, so the first ItemsDone of them are gone. Tell the views as the job runs,
    // not only at the end, so the rows leave the list while it works.
    private void AnnounceDeleted(JobSnapshot snapshot)
    {
        if (snapshot.Kind != JobKind.Delete || !_jobs.TryGetValue(snapshot.Id, out var p))
        {
            return;
        }

        List<LocationPath> gone;
        lock (p)
        {
            var upTo = Math.Min(snapshot.ItemsDone, p.Sources.Count);
            if (upTo <= p.Announced)
            {
                return;
            }

            gone = [.. p.Sources.Skip(p.Announced).Take(upTo - p.Announced)];
            p.Announced = upTo;
        }

        ItemsDeleted?.Invoke(this, gone);
    }

    private void Handle(JobSnapshot snapshot)
    {
        if (_jobs.TryRemove(snapshot.Id, out var p))
        {
            if (p.Kind == JobKind.Delete && !p.Permanent)
            {
                _ = ReportDeletedAsync(snapshot.Id);
            }

            Finished?.Invoke(this, new TransferFinished(p.Kind, p.Sources, p.Destination, p.Permanent, snapshot));
        }
    }

    /// <summary>Tells the undo history where each item went (spec §12.5). Items the bin gave no place for cannot be undone, so they are left out.</summary>
    private async Task ReportDeletedAsync(Guid jobId)
    {
        try
        {
            var result = await Connection().GetResultAsync(jobId).ConfigureAwait(false);
            var moves = WorkerUndoActions.Summarize(result.Items).Done;
            if (moves.Count > 0)
            {
                DeleteCompleted?.Invoke(this, moves);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The delete itself is done; only undo is lost.
        }
    }

    private sealed record Pending(JobKind Kind, IReadOnlyList<LocationPath> Sources, LocationPath? Destination, bool Permanent)
    {
        /// <summary>How many of the sources of a delete the views were already told about.</summary>
        public int Announced { get; set; }
    }
}
