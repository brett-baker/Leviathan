using Leviathan.Core.Paths;

namespace Leviathan.Core.Platform;

/// <summary>What the Delete key does for an item (spec §12.5), by where it lives.</summary>
public enum DeleteBehavior
{
    /// <summary>The Windows Recycle Bin: a local fixed drive.</summary>
    RecycleBin,

    /// <summary>A rename into the share's recycle folder (or Leviathan's own): a network share, or a drive letter mapped to one.</summary>
    SoftDelete,

    /// <summary>
    /// The place has no bin and Leviathan keeps none there (removable, optical or RAM drives, or a drive nothing is known about), so
    /// Delete is permanent and must be confirmed first, as Windows does for a USB stick.
    /// </summary>
    PermanentAfterConfirmation,
}

/// <summary>
/// The Delete vs Shift+Delete table of spec §12.5. Shift+Delete is always permanent after a confirmation; this decides plain Delete.
/// Pure, so the window (which asks for the confirmation) and the worker (which refuses to delete for good without it) agree.
/// Windows asks nothing when the Recycle Bin call is told to be silent and the drive has no bin: it deletes for good. That is why a
/// mapped drive or a USB stick must never be handed to it.
/// </summary>
public static class DeletePolicy
{
    /// <summary>What plain Delete does for <paramref name="location"/>; <paramref name="drive"/> is what is known about its drive letter, if anything.</summary>
    public static DeleteBehavior Decide(LocationPath location, DriveDetails? drive)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Kind == LocationKind.Unc)
        {
            return DeleteBehavior.SoftDelete;
        }

        return drive?.Kind switch
        {
            DriveKind.Fixed => DeleteBehavior.RecycleBin,
            DriveKind.Network => DeleteBehavior.SoftDelete,
            _ => DeleteBehavior.PermanentAfterConfirmation,
        };
    }

    /// <summary>The most careful of several behaviours: asking for a confirmation wins over a bin.</summary>
    public static DeleteBehavior Strictest(IEnumerable<DeleteBehavior> behaviors)
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        return behaviors.Aggregate(DeleteBehavior.RecycleBin, (a, b) => a == DeleteBehavior.PermanentAfterConfirmation || b == DeleteBehavior.PermanentAfterConfirmation
            ? DeleteBehavior.PermanentAfterConfirmation
            : a == DeleteBehavior.SoftDelete || b == DeleteBehavior.SoftDelete ? DeleteBehavior.SoftDelete : DeleteBehavior.RecycleBin);
    }
}
