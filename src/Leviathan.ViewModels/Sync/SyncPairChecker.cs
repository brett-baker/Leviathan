using Leviathan.Core.Compare;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Sync;

/// <summary>
/// Checks saved sync pairs on their schedule (spec §15.5: "optional schedule"). A check compares the two folders in the worker, plans
/// the pair's mode and records what is waiting, which the sidebar shows. It changes nothing, with one exception: a pair set to run on its
/// own whose mode is an update (see <see cref="SyncSchedule.MayRunUnattended"/>) has its steps queued as ordinary jobs, because an
/// update only adds and replaces older files with newer ones. Mirrors and two-way syncs are only ever checked here: they delete or can
/// hit conflicts, so a person sees the preview first. Time goes through <see cref="TimeProvider"/>; one check runs at a time.
/// </summary>
public sealed class SyncPairChecker(SyncPairStore store, ICompareScanner scanner, SyncRunner runner, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _one.Dispose();

    /// <summary>Checks every pair that is due now; returns how many were checked.</summary>
    public async Task<int> CheckDueAsync(CancellationToken cancellationToken)
    {
        if (!await _one.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        try
        {
            var due = SyncSchedule.Due(store.Items, time.GetUtcNow());
            foreach (var pair in due)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Marked first, so a slow check is not started again by the next tick.
                store.RecordCheck(pair.Id, time.GetUtcNow(), "checking…");
                await CheckCoreAsync(pair, cancellationToken).ConfigureAwait(false);
            }

            return due.Count;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Checks <paramref name="pair"/> now, whatever its schedule, and returns what was found.</summary>
    public async Task<string> CheckNowAsync(SyncPair pair, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pair);
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            store.RecordCheck(pair.Id, time.GetUtcNow(), "checking…");
            return await CheckCoreAsync(pair, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<string> CheckCoreAsync(SyncPair pair, CancellationToken cancellationToken)
    {
        string result;
        try
        {
            var options = new CompareOptions { By = pair.By, TimeTolerance = TimeSpan.FromSeconds(pair.ToleranceSeconds) };
            var compared = await scanner.CompareAsync(pair.Left, pair.Right, options, null, cancellationToken).ConfigureAwait(false);
            var plan = SyncPlanner.Plan(compared, pair.Mode);
            result = SyncSchedule.Summarize(plan);
            if (!plan.IsEmpty && SyncSchedule.MayRunUnattended(pair))
            {
                var outcome = await runner.RunAsync(plan, pair.Left, pair.Right, cancellationToken).ConfigureAwait(false);
                result = outcome.Started ? $"synced on its own: {outcome.CopyJobs:N0} copy jobs queued" : $"{result}; could not start: {(outcome.Left.Count > 0 ? outcome.Left[0] : "unknown reason")}";
            }
        }
        catch (OperationCanceledException)
        {
            store.RecordCheck(pair.Id, time.GetUtcNow(), "check stopped");
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException)
        {
            result = $"could not check: {ex.Message}";
        }

        store.RecordCheck(pair.Id, time.GetUtcNow(), result);
        return result;
    }
}
