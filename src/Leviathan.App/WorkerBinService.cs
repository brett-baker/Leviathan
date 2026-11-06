using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Hub;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.App;

/// <summary>
/// The Soft-delete bin's seam to the worker (spec §12.5): listing, restoring and purging are worker jobs, journaled and reported like
/// any other, so the window never touches a share. The automatic purge asks through <see cref="SubmitPolicyAsync"/>, as a background job.
/// </summary>
internal sealed class WorkerBinService(WorkerTransfers transfers, Func<FilesSettings> settings) : IBinService
{
    /// <inheritdoc />
    public Task<BinListing> ListAsync(CancellationToken cancellationToken) => transfers.GetBinAsync(cancellationToken);

    /// <inheritdoc />
    public Task<TransferOutcome> RestoreAsync(IReadOnlyList<BinEntryInfo> items, CancellationToken cancellationToken)
    {
        // The job moves each trashed item to where it came from; it never overwrites, and says so for any that is in the way.
        var job = new JobRequest(JobKind.Restore, [.. items.Select(i => i.Trashed)], null) { Targets = [.. items.Select(i => i.Original)] };
        var what = items.Count == 1 ? LocationPath.TryParse(items[0].Original, out var one) ? one.Name : "the item" : $"{items.Count} items";
        return transfers.SubmitTrackedAsync(job, Parse(items.Select(i => i.Original)), $"Restoring {what}…", cancellationToken);
    }

    /// <inheritdoc />
    public Task<TransferOutcome> PurgeAsync(IReadOnlyList<BinEntryInfo> items, CancellationToken cancellationToken)
    {
        var job = new JobRequest(JobKind.Purge, [.. items.Select(i => i.Trashed)], null);
        return transfers.SubmitTrackedAsync(job, Parse(items.Select(i => i.Trashed)), $"Deleting {(items.Count == 1 ? "1 item" : $"{items.Count} items")} for good…", cancellationToken);
    }

    /// <inheritdoc />
    public Task<TransferOutcome> PurgeOldAsync(CancellationToken cancellationToken)
    {
        var policy = settings().BinPolicy();
        return policy.MaxAgeDays <= 0 && policy.MaxBytes <= 0
            ? Task.FromResult(new TransferOutcome(false, "Both limits are off. Set how long to keep deleted items in Settings first."))
            : transfers.SubmitTrackedAsync(PolicyJob(policy), [], "Emptying old items from the bin…", cancellationToken);
    }

    /// <summary>The automatic purge: a background job by the rule, which leaves a report and is not an entry in the activity log.</summary>
    public async Task SubmitPolicyAsync(PurgePolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => transfers.SubmitUntrackedAsync(PolicyJob(policy)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or TimeoutException or InvalidOperationException))
        {
            // A broken pipe surfaces as a StreamJsonRpc exception; to the schedule it is only "the worker is away".
            throw new IOException(ex.Message, ex);
        }
    }

    private static JobRequest PolicyJob(PurgePolicy policy) => new(JobKind.Purge, [], null)
    {
        PurgePolicy = policy,
        Title = "Empty old items from the Soft-delete bin",
    };

    private static List<LocationPath> Parse(IEnumerable<string> paths) =>
        [.. paths.Select(p => LocationPath.TryParse(p, out var path) ? path : null).OfType<LocationPath>()];
}
