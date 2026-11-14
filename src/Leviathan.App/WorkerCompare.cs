using Leviathan.Contracts;
using Leviathan.Core.Compare;
using Leviathan.ViewModels.Sync;

namespace Leviathan.App;

/// <summary>
/// Runs the worker's compare job for the sync window (spec §15.5): listing two trees and reading files is the worker's work, never the
/// window's. Progress is passed on as one line of text; cancelling the window's token cancels the job. The job runs in the background
/// lane, so it never takes a slot from a copy.
/// </summary>
internal sealed class WorkerCompare(Func<WorkerConnection> worker) : ICompareScanner
{
    /// <inheritdoc />
    public async Task<CompareResult> CompareAsync(string left, string right, CompareOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var connection = worker();
        var request = new JobRequest(JobKind.Compare, [left], right)
        {
            CompareByChecksum = options.By == CompareBy.Checksum,
            CompareToleranceSeconds = (int)options.TimeTolerance.TotalSeconds,
            CompareIgnoreDaylightSaving = options.IgnoreDaylightSaving,
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
            else
            {
                progress?.Report(snapshot.CurrentItem is { } current ? $"Reading {current}" : $"{Math.Max(0, snapshot.ItemsDone):N0} items listed…");
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
                    throw new IOException(final.Error ?? "The comparison failed.");
                }
            }

            var result = await connection.GetResultAsync(submitted.Id).ConfigureAwait(false);
            var report = await connection.GetReportAsync(submitted.Id).ConfigureAwait(false);
            var entries = await Task.Run(() => result.Items.Select(i => CompareCodec.Decode(i.Source, i.Result)).OfType<CompareEntry>().ToList(), cancellationToken).ConfigureAwait(false);
            var problems = report?.Notes.Where(n => n.StartsWith("Could not read", StringComparison.Ordinal)).ToList() ?? [];
            return new CompareResult(entries, problems);
        }
        finally
        {
            connection.JobChanged -= OnChanged;
        }
    }
}
