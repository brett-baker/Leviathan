using Leviathan.Contracts;
using Leviathan.Core.Hashing;
using Leviathan.ViewModels.Checksums;

namespace Leviathan.App;

/// <summary>
/// Runs the worker's checksum job for the checksums window (spec §15.4): hashing reads files, so it is the worker's, never the window's.
/// Progress is passed on as one line of text; cancelling the window's token cancels the job. The job runs in the background lane, so it
/// never takes a slot from a copy.
/// </summary>
internal sealed class WorkerChecksums(Func<WorkerConnection> worker) : IChecksumScanner
{
    /// <inheritdoc />
    public async Task<ChecksumRun> RunAsync(IReadOnlyList<string> sources, IReadOnlyList<ChecksumKind> kinds, bool verify, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var connection = worker();
        var request = new JobRequest(JobKind.Checksum, sources, null)
        {
            ChecksumKinds = [.. kinds.Select(k => k.ToString())],
            ChecksumVerify = verify,
        };

        var ended = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.Empty;
        var early = new List<JobSnapshot>();

        void Handle(JobSnapshot snapshot)
        {
            if (snapshot.Id != id)
            {
                return;
            }

            if (snapshot.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
            {
                ended.TrySetResult(snapshot);
            }
            else if (snapshot.CurrentItem is { } current)
            {
                progress?.Report(snapshot.BytesTotal > 0 ? $"{snapshot.BytesDone * 100 / snapshot.BytesTotal}% · {current}" : current);
            }
        }

        void OnChanged(object? sender, JobSnapshot snapshot)
        {
            lock (early)
            {
                if (id == Guid.Empty)
                {
                    early.Add(snapshot);
                    return;
                }
            }

            Handle(snapshot);
        }

        connection.JobChanged += OnChanged;
        try
        {
            var submitted = await Task.Run(() => connection.SubmitAsync(request), cancellationToken).ConfigureAwait(false);
            List<JobSnapshot> missed;
            lock (early)
            {
                id = submitted.Id;
                missed = [.. early];
            }

            foreach (var snapshot in missed)
            {
                Handle(snapshot);
            }

            Handle(submitted);
            await using (cancellationToken.Register(() => _ = connection.CancelAsync(submitted.Id)))
            {
                var final = await ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (final.State == JobState.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (final.State == JobState.Failed)
                {
                    throw new IOException(final.Error ?? "The checksum job failed.");
                }
            }

            var result = await connection.GetResultAsync(submitted.Id).ConfigureAwait(false);
            var report = await connection.GetReportAsync(submitted.Id).ConfigureAwait(false);
            return new ChecksumRun(ChecksumView.RowsFrom(result.Items), report?.Notes ?? []);
        }
        finally
        {
            connection.JobChanged -= OnChanged;
        }
    }
}
