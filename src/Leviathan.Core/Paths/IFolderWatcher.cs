namespace Leviathan.Core.Paths;

/// <summary>
/// Tells the app when a folder it shows changed behind its back (spec §3.1: a listing on screen follows the disk). Local drives
/// report changes as they happen; network shares are polled with a single cheap stat of the folder's time, backing off while
/// nothing changes, so they cost almost no traffic (spec §3.3).
/// </summary>
public interface IFolderWatcher
{
    /// <summary>
    /// Starts reporting changes to <paramref name="folder"/> (its direct children). <paramref name="changed"/> is called on a
    /// thread-pool thread, already debounced: a burst of changes gives one call. A folder that cannot be watched returns a
    /// subscription that does nothing, never an exception.
    /// </summary>
    /// <param name="folder">The folder to watch.</param>
    /// <param name="changed">Called when something in it was added, removed, renamed or modified.</param>
    /// <returns>Stops the watching when disposed.</returns>
    IDisposable Watch(LocationPath folder, Action changed);
}
