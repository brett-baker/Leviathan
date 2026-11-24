namespace Leviathan.ViewModels.Triage;

/// <summary>
/// The sentences triage shows and logs (spec §10.1, §16.1): where Keep puts files, and what each decision did and where the file is now.
/// Kept pure so the wording is tested and the window only displays it. A decision is never described by the source alone: the log
/// says where the file went, or says plainly that it stayed.
/// </summary>
public static class TriageNotes
{
    /// <summary>The line at the top of the triage window: where kept files go, or why they go nowhere.</summary>
    /// <param name="destination">The folder kept files go to, or null.</param>
    /// <param name="moveOnKeep">True when each file moves as it is kept; false when they move together when triage ends.</param>
    public static string DestinationLine(string? destination, bool moveOnKeep)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return "Keep has no destination: kept files are only tagged green and stay where they are. Open a split pane on the destination, or set a keep folder in Settings.";
        }

        return moveOnKeep
            ? $"Keep moves files to {destination.Trim()}"
            : $"Kept files are moved to {destination.Trim()} when triage ends";
    }

    /// <summary>True when <see cref="DestinationLine"/> is a warning (nowhere to put files).</summary>
    public static bool IsWarning(string? destination) => string.IsNullOrWhiteSpace(destination);

    /// <summary>What a Keep did, for the Activity tab: "Kept a.mkv: moved from D:\in to D:\keep" or why it stayed.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="fromFolder">The folder it was in.</param>
    /// <param name="toFolder">The folder it is in now; equal to <paramref name="fromFolder"/> when it did not move.</param>
    /// <param name="destination">The destination in force when it was kept, or null.</param>
    /// <param name="moveOnKeep">Whether each file moves as it is kept.</param>
    public static string Kept(string name, string fromFolder, string toFolder, string? destination, bool moveOnKeep)
    {
        if (!SameFolder(fromFolder, toFolder))
        {
            return $"Triage kept {name}: moved from {fromFolder} to {toFolder}";
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            return $"Triage kept {name}: tagged green and left in {fromFolder}, because there is no destination folder";
        }

        return moveOnKeep
            ? $"Triage kept {name}: tagged green; the move to {destination.Trim()} is queued and will show in Operations"
            : $"Triage kept {name}: tagged green and left in {fromFolder}; it moves to {destination.Trim()} when triage ends";
    }

    /// <summary>What a Discard did: "Discarded a.mkv from D:\in: moved to the bin, Ctrl+Z restores it".</summary>
    public static string Discarded(string name, string fromFolder) =>
        $"Triage discarded {name} from {fromFolder}: moved to the bin (Ctrl+Z restores it)";

    /// <summary>What taking a decision back did.</summary>
    public static string TakenBack(string name, string folder) => $"Triage took back the decision on {name}: it is in {folder}";

    private static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
