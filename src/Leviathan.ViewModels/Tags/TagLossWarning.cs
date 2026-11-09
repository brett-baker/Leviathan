using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Transfers;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tags;


/// <summary>
/// Puts a note on the status line when a copy or move sends tagged files where tags cannot follow (spec §11.2 rule 5: "Tags may be
/// lost"). It never stops the transfer: the files still arrive, only their colour tags do not. Only the dragged or pasted files
/// themselves are checked; the contents of folders are not looked through, which would mean walking them before the copy starts.
/// </summary>
/// <param name="inner">The real service.</param>
/// <param name="tags">Where tags are read.</param>
/// <param name="streams">Tells what the destination can hold.</param>
public sealed class TagLossWarningTransfers(ITransferService inner, ITagStore tags, IStreamSupport streams) : ITransferService
{
    /// <summary>How many sources are looked at; a drop of thousands of files does not read thousands of streams before it starts.</summary>
    public const int MaxChecked = 200;

    /// <inheritdoc />
    public async Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lost = request.Mode != TransferMode.Link && await WouldLoseTagsAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = await inner.StartAsync(request, cancellationToken).ConfigureAwait(false);
        return lost && outcome.Started
            ? outcome with { Message = outcome.Message + " Tags may be lost: " + request.Destination.Name + " cannot keep colour tags." }
            : outcome;
    }

    private async Task<bool> WouldLoseTagsAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (await streams.SupportsStreamsAsync(request.Destination, cancellationToken).ConfigureAwait(false) is not false)
            {
                return false;
            }

            foreach (var source in request.Sources.Take(MaxChecked))
            {
                if ((await tags.GetAsync(source.ToWin32Path(), cancellationToken).ConfigureAwait(false)).Count > 0)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // A source whose tags cannot be read is treated as untagged: the warning is a courtesy, never a reason to fail.
        }

        return false;
    }
}
