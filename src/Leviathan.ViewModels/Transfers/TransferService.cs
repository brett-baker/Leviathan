using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Transfers;

/// <summary>Items to put in a folder and how (spec §8.4, §12.1).</summary>
/// <param name="Sources">The dragged or pasted items.</param>
/// <param name="Destination">The folder they go into.</param>
/// <param name="Mode">Move, copy or shortcut.</param>
public sealed record TransferRequest(IReadOnlyList<LocationPath> Sources, LocationPath Destination, TransferMode Mode);

/// <summary>What became of a <see cref="TransferRequest"/>.</summary>
/// <param name="Started">True when the work was handed on; false when nothing was done.</param>
/// <param name="Message">Text for the status bar.</param>
public sealed record TransferOutcome(bool Started, string Message);

/// <summary>
/// Carries out drops and pastes. This is the seam between the user interface, which only decides <em>what</em> should happen,
/// and the journaled worker that does it (spec §12.1: nothing is lost, a cross-volume move deletes the source only after the copy
/// is verified). Slice E2 supplies the real implementation.
/// </summary>
public interface ITransferService
{
    /// <summary>Starts the transfer and returns once it is queued; it never blocks on the copying itself.</summary>
    Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Stands in until the copy and move slice (E2) lands: it refuses every request and says why, so a drop is never silently
/// ignored and nothing is half-copied by code that skips the journal.
/// </summary>
public sealed class UnavailableTransferService : ITransferService
{
    /// <inheritdoc />
    public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verb = request.Mode switch
        {
            TransferMode.Move => "Moving",
            TransferMode.Link => "Creating shortcuts",
            _ => "Copying",
        };
        return Task.FromResult(new TransferOutcome(false, $"{verb} files arrives with the next update. Nothing was changed."));
    }
}

/// <summary>
/// Deletes items (spec §12.5). The seam to the worker's delete job: the user interface decides <em>what</em> and whether it was
/// confirmed; the worker moves items to the Recycle Bin or a share's recycle folder, or deletes them for good.
/// </summary>
public interface IDeleteService
{
    /// <summary>Starts the delete and returns once it is queued.</summary>
    Task<TransferOutcome> DeleteAsync(IReadOnlyList<LocationPath> items, bool permanent, CancellationToken cancellationToken);

    /// <summary>
    /// What plain Delete would do with these items (spec §12.5), the most careful answer when they differ, so the window can ask for a
    /// confirmation before a delete that has no bin. Defaults to the Recycle Bin for services that do not know.
    /// </summary>
    Task<Leviathan.Core.Platform.DeleteBehavior> BehaviorAsync(IReadOnlyList<LocationPath> items, CancellationToken cancellationToken) =>
        Task.FromResult(Leviathan.Core.Platform.DeleteBehavior.RecycleBin);
}
