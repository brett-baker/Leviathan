using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Triage;

/// <summary>Kept files that are still waiting to be moved to one folder.</summary>
/// <param name="Folder">The folder they were to be moved to.</param>
/// <param name="Files">The files, still where triage left them.</param>
public sealed record PendingMoveGroup(string Folder, IReadOnlyList<FileEntry> Files);

/// <summary>What the journal says is still to do after a restart.</summary>
/// <param name="Groups">Kept files waiting for a move, by destination folder.</param>
/// <param name="Stale">Journal paths whose file is no longer there (moved or deleted since); they are marked as dealt with.</param>
public sealed record PendingKeepMoves(IReadOnlyList<PendingMoveGroup> Groups, IReadOnlyList<string> Stale)
{
    /// <summary>Number of files waiting.</summary>
    public int Count => Groups.Sum(g => g.Files.Count);

    /// <summary>
    /// Works out what to offer from the journal's pending moves (spec §10.1, §3.4): kept files that were to be moved when triage ended
    /// and never were, for instance because the app crashed first. A file that has gone since is stale, not pending.
    /// </summary>
    /// <param name="pending">The journal's <see cref="TriageJournal.PendingMoves"/>.</param>
    /// <param name="exists">Whether a file is still there (a Win32 path); the real one asks the disk off the UI thread.</param>
    public static PendingKeepMoves From(IReadOnlyList<JournalRecord> pending, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(exists);
        var stale = new List<string>();
        var byFolder = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in pending.Where(r => r.IsPendingMove))
        {
            var location = LocationPath.Parse(record.Path);
            if (!exists(location.ToWin32Path()))
            {
                stale.Add(record.Path);
                continue;
            }

            var folder = record.MoveTo!.Trim();
            if (!byFolder.TryGetValue(folder, out var list))
            {
                byFolder[folder] = list = [];
            }

            var now = DateTimeOffset.UtcNow;
            list.Add(new FileEntry(location.Name, location, FileEntryKind.File, 0, now, now, FileAttributes.Normal));
        }

        return new PendingKeepMoves([.. byFolder.Select(p => new PendingMoveGroup(p.Key, p.Value))], stale);
    }
}
