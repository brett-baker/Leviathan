using Leviathan.Core.Model;

namespace Leviathan.ViewModels.Triage;

/// <summary>What has been decided about one file of the queue.</summary>
public enum TriageDecision
{
    /// <summary>Not looked at yet.</summary>
    None,

    /// <summary>Kept: green tag, and moved to the keep folder when the setting says so.</summary>
    Kept,

    /// <summary>Discarded: soft-deleted, so it can be restored.</summary>
    Discarded,

    /// <summary>Looked at and left for later: no decision, no change to the file.</summary>
    Skipped,
}

/// <summary>
/// One file in the queue and what became of it (spec §10.1). Owned by <see cref="TriageSession"/>, which is the only thing that changes it.
/// </summary>
public sealed class TriageItem(FileEntry file)
{
    /// <summary>The file; after a keep that moved it, its new place.</summary>
    public FileEntry File { get; internal set; } = file;

    /// <summary>The file as it was when the queue was made, for putting it back.</summary>
    public FileEntry Original { get; } = file;

    /// <summary>The decision, <see cref="TriageDecision.None"/> until one is made.</summary>
    public TriageDecision Decision { get; internal set; }

    /// <summary>The tags (1 to 9) toggled on this file during the session.</summary>
    public HashSet<int> Tags { get; } = [];

    /// <summary>Takes the decision back: restores a discarded file, removes the green tag, moves a kept file home. Null when there is nothing to take back.</summary>
    internal Func<Task>? Undo { get; set; }

    /// <summary>True when the file can be opened in the player: a discarded file is in the recycle area, not where it was.</summary>
    public bool IsPlayable => Decision != TriageDecision.Discarded;
}

/// <summary>What happened when a decision was applied to the file system and the tag index.</summary>
/// <param name="Undo">Takes the action back; null when it cannot be.</param>
/// <param name="Moved">The file in its new place when the action moved it (a keep with a target folder), else null.</param>
public sealed record TriageActionResult(Func<Task>? Undo, FileEntry? Moved = null);

/// <summary>
/// What a decision does to files and tags (spec §10.1, D17, D26): Keep puts the green tag on (and moves the file when a keep folder is
/// set), Discard is a soft delete through the Phase 2 engine, a number key toggles a colour tag. Behind an interface so the session is
/// tested without a file system, and so the real thing (E3) can be built on the worker and the tag index. Actions may throw
/// <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> or <see cref="InvalidOperationException"/>; the session then keeps
/// the file and shows the message instead of moving on.
/// </summary>
public interface ITriageActions
{
    /// <summary>Keeps <paramref name="file"/>.</summary>
    Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken);

    /// <summary>Discards <paramref name="file"/> (soft delete). The file is not open in the player when this runs.</summary>
    Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken);

    /// <summary>Toggles colour tag <paramref name="tagNumber"/> (1 to 9) on <paramref name="file"/>; returns true when it is on afterwards.</summary>
    Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken);

    /// <summary>Removes every colour tag from <paramref name="file"/> (Ctrl+0).</summary>
    Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken);
}

/// <summary>How a triage session behaves (spec §10.1 and the Media settings of §17).</summary>
public sealed record TriageOptions
{
    /// <summary>Each file starts this far in (0 to 0.9 of its length) to skip intros; 0 starts at the beginning. Default 10%.</summary>
    public double StartOffset { get; init; } = 0.10;

    /// <summary>Shortest file the start offset is applied to; in a clip shorter than this the intro is the whole thing.</summary>
    public TimeSpan OffsetMinimumDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after a decision the next file opens (0 to 1 s); null means do not advance, the user moves on.</summary>
    public TimeSpan? AutoAdvanceDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Limit the queue to files with no Keep or Discard decision yet, so a session can resume where it stopped.</summary>
    public bool UntriagedOnly { get; init; }

    /// <summary>Include the videos in sub-folders when the queue is built from a folder.</summary>
    public bool Recursive { get; init; }
}

/// <summary>How a session ended, for the summary at the end of the queue (spec §10.1: "Review discarded", "Purge discarded now").</summary>
/// <param name="Kept">Files kept.</param>
/// <param name="Discarded">Files discarded.</param>
/// <param name="Skipped">Files skipped.</param>
/// <param name="Undecided">Files never reached.</param>
/// <param name="ReclaimedBytes">Bytes in the discarded files.</param>
/// <param name="DiscardedFiles">The discarded files, for review and restore.</param>
public sealed record TriageSummary(int Kept, int Discarded, int Skipped, int Undecided, long ReclaimedBytes, IReadOnlyList<FileEntry> DiscardedFiles);
