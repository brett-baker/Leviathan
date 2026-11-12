using Leviathan.Contracts;
using Leviathan.ViewModels.Duplicates;

namespace Leviathan.App;

/// <summary>
/// Runs the worker's find-duplicates job for the results window (spec §15.2): the search reads files, so it is the worker's, never the
/// window's. Progress is passed on as one line of text; cancelling the window's token cancels the job. The file times the groups need
/// are read on the thread pool after the job ends.
/// </summary>
internal sealed class WorkerDuplicateScanner(Func<WorkerConnection> worker) : IDuplicateScanner
{
    /// <inheritdoc />
    public async Task<DuplicateScan> ScanAsync(IReadOnlyList<string> roots, long minimumBytes, IReadOnlyList<string>? extensions, bool includeHidden, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var connection = worker();
        var request = new JobRequest(JobKind.FindDuplicates, roots, null)
        {
            DuplicateMinimumBytes = minimumBytes,
            DuplicateExtensions = extensions,
            DuplicateIncludeHidden = includeHidden,
        };

        var ended = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid id = Guid.Empty;
        var early = new List<JobSnapshot>();

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
                progress?.Report(current);
            }
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
                    throw new IOException(final.Error ?? "The search failed.");
                }
            }

            var result = await connection.GetResultAsync(submitted.Id).ConfigureAwait(false);
            var report = await connection.GetReportAsync(submitted.Id).ConfigureAwait(false);
            var groups = await Task.Run(() => DuplicateReview.GroupsFrom(result.Items, ModifiedOf), cancellationToken).ConfigureAwait(false);
            var notes = new List<string>();
            notes.AddRange(result.Items.Where(i => i.Status != ItemStatus.Done).Select(i => i.Error ?? i.Source));
            if (report?.Notes is { Count: > 0 } reported)
            {
                notes.AddRange(reported);
            }

            return new DuplicateScan(groups, notes);
        }
        finally
        {
            connection.JobChanged -= OnChanged;
        }
    }

    private static DateTime ModifiedOf(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MaxValue;
        }
    }
}
