using Leviathan.Core.Compare;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Sync;

/// <summary>Compares two folders in the worker for the sync window (spec §15.5): the seam between the window and the worker.</summary>
public interface ICompareScanner
{
    /// <summary>Compares <paramref name="left"/> with <paramref name="right"/> (OS paths); <paramref name="progress"/> gets one line of text; cancelling the token cancels the job.</summary>
    Task<CompareResult> CompareAsync(string left, string right, CompareOptions options, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>What starting a sync did.</summary>
/// <param name="CopyJobs">How many copy jobs were queued.</param>
/// <param name="Deleted">How many items were handed to the delete job.</param>
/// <param name="Left">Steps that were not started, with the reason: a type change, a name that cannot be addressed, a job the worker refused.</param>
public sealed record SyncRunOutcome(int CopyJobs, int Deleted, IReadOnlyList<string> Left)
{
    /// <summary>True when something was queued.</summary>
    public bool Started => CopyJobs > 0 || Deleted > 0;
}

/// <summary>
/// Carries out a confirmed <see cref="SyncPlan"/> (spec §15.5) with the worker's ordinary jobs, so a sync is journaled, verified,
/// resumable and shown in the Operations panel like any other copy: files are copied into their target folder with replace-without-asking
/// (the preview already said what is overwritten), one job for each target folder, and deletions are soft deletes (the Recycle Bin or the
/// share's recycle folder, undoable). Deletions are queued first. A step that changes an item's type (a file where a folder was) is
/// not started: it needs the delete to finish before the copy, which separate jobs cannot promise, so it is listed for the person to do.
/// </summary>
public sealed class SyncRunner(ITransferService transfers, IDeleteService deleter)
{
    /// <summary>Queues the steps of <paramref name="plan"/> between <paramref name="leftRoot"/> and <paramref name="rightRoot"/> (OS paths).</summary>
    public async Task<SyncRunOutcome> RunAsync(SyncPlan plan, string leftRoot, string rightRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var left = new List<string>();
        var typeChanges = plan.Actions.GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in typeChanges)
        {
            left.Add($"{path}: a file and a folder swap places here; delete the old one by hand, then sync again.");
        }

        var deleted = 0;
        foreach (var side in new[] { true, false })
        {
            var kind = side ? SyncActionKind.DeleteRight : SyncActionKind.DeleteLeft;
            var root = side ? rightRoot : leftRoot;
            var items = new List<LocationPath>();
            foreach (var action in plan.Of(kind).Where(a => !typeChanges.Contains(a.Path)))
            {
                if (TryLocation(root, action.Path, left) is { } location)
                {
                    items.Add(location);
                }
            }

            if (items.Count == 0)
            {
                continue;
            }

            var outcome = await deleter.DeleteAsync(items, permanent: false, cancellationToken).ConfigureAwait(false);
            if (outcome.Started)
            {
                deleted += items.Count;
            }
            else
            {
                left.Add(outcome.Message);
            }
        }

        var jobs = 0;
        foreach (var toRight in new[] { true, false })
        {
            var from = toRight ? leftRoot : rightRoot;
            var into = toRight ? rightRoot : leftRoot;
            var kind = toRight ? SyncActionKind.CopyToRight : SyncActionKind.CopyToLeft;
            var groups = new Dictionary<string, List<LocationPath>>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in plan.Of(kind).Where(a => !typeChanges.Contains(a.Path)))
            {
                var parent = action.Path.Contains('\\', StringComparison.Ordinal) ? action.Path[..action.Path.LastIndexOf('\\')] : string.Empty;
                if (TryLocation(from, action.Path, left) is not { } source)
                {
                    continue;
                }

                if (!groups.TryGetValue(parent, out var sources))
                {
                    groups[parent] = sources = [];
                }

                sources.Add(source);
            }

            foreach (var (parent, sources) in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (TryLocation(into, parent, left, allowEmpty: true) is not { } destination)
                {
                    continue;
                }

                var outcome = await transfers.StartAsync(new TransferRequest(sources, destination, TransferMode.Copy) { Replace = true }, cancellationToken).ConfigureAwait(false);
                if (outcome.Started)
                {
                    jobs++;
                }
                else
                {
                    left.Add(outcome.Message);
                }
            }
        }

        return new SyncRunOutcome(jobs, deleted, left);
    }

    private static LocationPath? TryLocation(string root, string relative, List<string> problems, bool allowEmpty = false)
    {
        try
        {
            return LocationPath.Parse(relative.Length == 0 && allowEmpty ? root : Path.Combine(root, relative));
        }
        catch (FormatException)
        {
            problems.Add($"{relative}: Windows cannot address this name.");
            return null;
        }
    }
}
