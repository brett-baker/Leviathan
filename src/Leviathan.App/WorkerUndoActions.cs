using Leviathan.Contracts;
using Leviathan.ViewModels.Undo;

namespace Leviathan.App;

/// <summary>
/// Does undo and redo in the worker (spec §12.1, §12.5): putting items back is a restore job and redoing a delete is a delete job,
/// so both are journaled like any other file operation, and the UI thread never touches the disk.
/// </summary>
internal sealed class WorkerUndoActions(Func<WorkerConnection> worker) : IUndoActions
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public Task<StepResult> MoveAsync(IReadOnlyList<ItemMove> moves, CancellationToken cancellationToken) =>
        RunAsync(new JobRequest(JobKind.Restore, [.. moves.Select(m => m.From)], null) { Targets = [.. moves.Select(m => m.To)] }, cancellationToken);

    /// <inheritdoc />
    public Task<StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        RunAsync(new JobRequest(JobKind.Delete, paths, null) { DeleteMode = DeleteMode.RecycleBin }, cancellationToken);

    private async Task<StepResult> RunAsync(JobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(() => worker().RunAsync(request, Limit, cancellationToken), cancellationToken).ConfigureAwait(false);
            return Summarize(result.Items);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            return new StepResult([], [ex.Message]);
        }
    }

    /// <summary>
    /// Turns the per-item results into moves. A restore reports where each item ended up. A delete done again reports where the item
    /// went; if it went somewhere unknown (a drive without a Recycle Bin) it is gone but can no longer be undone, which is said.
    /// </summary>
    internal static StepResult Summarize(IReadOnlyList<ItemOutcome> items)
    {
        var done = new List<ItemMove>();
        var problems = new List<string>();
        foreach (var item in items)
        {
            var name = item.Source.TrimEnd('\\').Split('\\')[^1];
            if (item.Status == ItemStatus.Done && item.Result is { } result)
            {
                done.Add(new ItemMove(item.Source, result));
            }
            else if (item.Status == ItemStatus.Done)
            {
                problems.Add($"'{name}' was deleted but cannot be restored.");
            }
            else
            {
                problems.Add(item.Error ?? $"'{name}' could not be done.");
            }
        }

        return new StepResult(done, problems);
    }
}
