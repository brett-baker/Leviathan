using Leviathan.Core.Archives;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.ViewModels.Commands;

/// <summary>
/// Where "Open terminal here" opens and which terminal to start (spec §15.7, D40). The folder is the selected folder, else the folder of
/// the selected file, else the folder shown. Only real folders on this PC or on a Windows share qualify: a registry location, search
/// results, a tag folder or the inside of an archive has no working directory, so the command is off there. Pure.
/// </summary>
public static class TerminalTargets
{
    /// <summary>The folder a terminal should start in, or null when there is none for this selection.</summary>
    /// <param name="selection">The selected items (the first one decides), or empty.</param>
    /// <param name="current">The folder shown.</param>
    public static LocationPath? FolderFor(IReadOnlyList<FileEntry> selection, LocationPath current)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(current);
        if (selection.Count > 0 && ArchiveKinds.IsInside(selection[0].Location))
        {
            return null;
        }

        LocationPath? candidate = selection.Count switch
        {
            0 => current,
            _ when selection[0].Kind is FileEntryKind.Folder || (selection[0].Kind == FileEntryKind.Link && selection[0].Attributes.HasFlag(FileAttributes.Directory)) => selection[0].Location,
            _ => selection[0].Location.Parent ?? current,
        };
        return IsUsable(candidate) ? candidate : null;
    }

    /// <summary>True for a folder on a drive or a share that is not inside an archive.</summary>
    public static bool IsUsable(LocationPath? folder) =>
        folder is not null && LocationKinds.IsFolderOnDriveOrShare(folder) && !ArchiveKinds.IsInside(folder);

    /// <summary>
    /// The terminal to try when <paramref name="wanted"/> could not be started, or null when there is nothing to fall back to. Windows
    /// Terminal is not on every PC (it is an app of its own), so it falls back to PowerShell, which every Windows has.
    /// </summary>
    public static TerminalKind? FallbackFor(TerminalKind wanted) => wanted == TerminalKind.WindowsTerminal ? TerminalKind.PowerShell : null;

    /// <summary>The name shown to people: "Windows Terminal", "PowerShell", "Command Prompt", "WSL".</summary>
    public static string Title(TerminalKind kind) => kind switch
    {
        TerminalKind.WindowsTerminal => "Windows Terminal",
        TerminalKind.PowerShell => "PowerShell",
        TerminalKind.CommandPrompt => "Command Prompt",
        _ => "WSL",
    };
}
