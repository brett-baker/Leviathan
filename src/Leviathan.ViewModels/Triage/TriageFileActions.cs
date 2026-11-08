using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Transfers;
using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Triage;

/// <summary>The colour tags of a file (spec §11), as far as triage needs them until the tag index of Phase F stands beside them.</summary>
public interface ITagStore
{
    /// <summary>The tags on the file; empty when it has none. Throws <see cref="IOException"/> when the location cannot be read.</summary>
    Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the tags on the file. Must leave the file's modified time as it was (spec §11.2 rule 1) and throw
    /// <see cref="IOException"/> rather than lose a tag silently when the location cannot hold them.
    /// </summary>
    Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken);
}

/// <summary>The tag ids triage uses. The default set of §11.1 in order: Red 1, Orange 2, Yellow 3, Green 4, Blue 5, Purple 6, Gray 7.</summary>
public static class TagIds
{
    /// <summary>Green, "Keep" (spec D17).</summary>
    public const int Green = 4;
}

/// <summary>Where Keep puts a file.</summary>
/// <param name="KeepFolder">The folder Keep also moves the file to, or null or empty to leave it where it is.</param>
public sealed record TriageKeepOptions(string? KeepFolder);

/// <summary>
/// What a decision really does to files (spec §10.1, D17, D26). Keep puts the green tag on the file and, when a keep folder is set,
/// moves it there: a rename when the folder is on the same drive or share (instant, even for 100 GB on a NAS), a queued move job when it
/// is not. Discard is a soft delete through the same journaled delete the file view uses, so the file goes to the Recycle Bin or the
/// share's recycle folder, never away for good. Both come back with an undo that does the exact opposite, which the session runs for
/// Ctrl+Z. A step that fails leaves things as they were: a tag written before a move that then failed is taken off again. Nothing here
/// touches the disk on the UI thread; the file work is the worker's and the tags go through <see cref="ITagStore"/>.
/// </summary>
public sealed class TriageFileActions(IUndoActions files, ITagStore tags, Func<TriageKeepOptions> keepOptions, ITransferService? transfers = null) : ITriageActions
{
    /// <inheritdoc />
    public async Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = file.Location.ToWin32Path();
        var before = await tags.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await tags.SetAsync(path, new HashSet<int>(before) { TagIds.Green }, cancellationToken).ConfigureAwait(false);

        var folder = keepOptions().KeepFolder;
        if (string.IsNullOrWhiteSpace(folder) || SameFolder(path, folder))
        {
            return new TriageActionResult(() => tags.SetAsync(path, before, CancellationToken.None));
        }

        var target = Path.Combine(folder.Trim(), file.Name);
        if (!SameVolume(path, folder))
        {
            return await QueueMoveAsync(file, path, folder, before, cancellationToken).ConfigureAwait(false);
        }

        StepResult moved;
        try
        {
            moved = await files.MoveAsync([new ItemMove(path, target)], cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRestoreTagsAsync(path, before).ConfigureAwait(false);
            throw;
        }

        if (moved.Done.Count == 0)
        {
            await TryRestoreTagsAsync(path, before).ConfigureAwait(false);
            throw new IOException(Problem(moved, $"{file.Name} could not be moved to {folder}."));
        }

        var final = moved.Done[0].To;
        var movedFile = file with { Location = LocationPath.Parse(final) };
        return new TriageActionResult(
            async () =>
            {
                var back = await files.MoveAsync([new ItemMove(final, path)], CancellationToken.None).ConfigureAwait(false);
                if (back.Done.Count == 0)
                {
                    throw new IOException(Problem(back, $"{file.Name} could not be moved back."));
                }

                await tags.SetAsync(path, before, CancellationToken.None).ConfigureAwait(false);
            },
            movedFile);
    }

    /// <inheritdoc />
    public async Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = file.Location.ToWin32Path();
        var deleted = await files.DeleteAsync([path], cancellationToken).ConfigureAwait(false);
        if (deleted.Done.Count == 0)
        {
            throw new IOException(Problem(deleted, $"{file.Name} could not be discarded."));
        }

        var binned = deleted.Done[0].To;
        return new TriageActionResult(async () =>
        {
            var back = await files.MoveAsync([new ItemMove(binned, path)], CancellationToken.None).ConfigureAwait(false);
            if (back.Done.Count == 0)
            {
                throw new IOException(Problem(back, $"{file.Name} could not be restored."));
            }
        });
    }

    /// <inheritdoc />
    public async Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = file.Location.ToWin32Path();
        var current = new HashSet<int>(await tags.GetAsync(path, cancellationToken).ConfigureAwait(false));
        var on = current.Add(tagNumber);
        if (!on)
        {
            current.Remove(tagNumber);
        }

        await tags.SetAsync(path, current, cancellationToken).ConfigureAwait(false);
        return on;
    }

    /// <inheritdoc />
    public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        return tags.SetAsync(file.Location.ToWin32Path(), new HashSet<int>(), cancellationToken);
    }

    // A keep folder on another drive or share is a copy, verify and delete: a job in the queue that can take hours, never awaited here.
    private async Task<TriageActionResult> QueueMoveAsync(FileEntry file, string path, string folder, IReadOnlySet<int> before, CancellationToken cancellationToken)
    {
        if (transfers is null)
        {
            await TryRestoreTagsAsync(path, before).ConfigureAwait(false);
            throw new InvalidOperationException($"{file.Name} is on another drive than the keep folder, and moving between drives is not available here.");
        }

        TransferOutcome outcome;
        try
        {
            outcome = await transfers.StartAsync(new TransferRequest([file.Location], LocationPath.Parse(folder.Trim()), TransferMode.Move), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRestoreTagsAsync(path, before).ConfigureAwait(false);
            throw;
        }

        if (!outcome.Started)
        {
            await TryRestoreTagsAsync(path, before).ConfigureAwait(false);
            throw new InvalidOperationException(outcome.Message);
        }

        // The move runs in the queue and cannot be taken back from here: Undo takes the tag off, and the job's own undo moves the file.
        return new TriageActionResult(() => tags.SetAsync(path, before, CancellationToken.None));
    }

    private async Task TryRestoreTagsAsync(string path, IReadOnlySet<int> before)
    {
        try
        {
            await tags.SetAsync(path, before, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The move already failed; the tag staying on is the lesser harm and the original error is the one to show.
        }
    }

    private static string Problem(StepResult result, string fallback) => result.Problems.Count > 0 ? result.Problems[0] : fallback;

    /// <summary>True when both paths are on the same drive or the same share, so a move between them is a rename.</summary>
    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a)?.TrimEnd('\\'), Path.GetPathRoot(b.Trim())?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool SameFolder(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\'), folder.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
