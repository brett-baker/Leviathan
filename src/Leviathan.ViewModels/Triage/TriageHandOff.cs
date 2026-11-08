using Leviathan.Core.Model;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Triage;

/// <summary>What closing triage should offer to do with the files that were kept.</summary>
/// <param name="Files">The kept files, in queue order.</param>
/// <param name="Folder">Where they would go.</param>
/// <param name="Ask">True to ask first; false when the user chose to always move.</param>
public sealed record TriageHandOffPlan(IReadOnlyList<FileEntry> Files, string Folder, bool Ask);

/// <summary>
/// Works out, when triage closes, whether the kept files should be moved to the destination (the other pane of a split tab, or the keep
/// folder) and whether to ask (spec §10.1). Moving on Keep, no destination or nothing kept all mean there is nothing to offer. Pure
/// logic so the window only asks the question and runs the move.
/// </summary>
public static class TriageHandOff
{
    /// <summary>The plan, or null when there is nothing to offer.</summary>
    /// <param name="session">The session that is ending.</param>
    /// <param name="media">The triage settings.</param>
    /// <param name="destination">Where kept files go now: the other pane's folder or the keep folder; empty or null for none.</param>
    public static TriageHandOffPlan? Plan(TriageSession session, MediaSettings media, string? destination)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(media);
        if (media.TriageMoveWhen != TriageMoveMode.AtEnd || string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        var folder = destination.Trim();
        var kept = session.Items
            .Where(i => i.Decision == TriageDecision.Kept)
            .Select(i => i.File)
            .Where(f => !string.Equals(f.Location.Parent?.ToWin32Path().TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return kept.Count == 0 ? null : new TriageHandOffPlan(kept, folder, media.TriageAskBeforeMove);
    }
}
