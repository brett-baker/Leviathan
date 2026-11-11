using Leviathan.Core.Model;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.CommandBar;

/// <summary>One icon button of the command bar (spec §6): a command, the glyph that draws it and the text of its tooltip.</summary>
/// <param name="Command">The command it runs. A command that is not registered yet leaves the button disabled.</param>
/// <param name="Title">Name shown in the tooltip and in the overflow menu.</param>
/// <param name="Glyph">Segoe Fluent Icons character.</param>
/// <param name="Shortcut">Key text for the tooltip, or null.</param>
public sealed record BarButton(CommandId Command, string Title, string Glyph, string? Shortcut = null)
{
    /// <summary>"Copy (Ctrl+C)": the tooltip the spec asks for, so icons stay compact (spec §6).</summary>
    public string Tooltip => Shortcut is null ? Title : $"{Title} ({Shortcut})";
}

/// <summary>What the selection is, for choosing the context actions (spec §6.2).</summary>
public enum SelectionClass
{
    /// <summary>Nothing selected.</summary>
    Nothing,

    /// <summary>One folder.</summary>
    Folder,

    /// <summary>Several items.</summary>
    Several,

    /// <summary>One video file.</summary>
    Video,

    /// <summary>One image.</summary>
    Image,

    /// <summary>One audio file.</summary>
    Audio,

    /// <summary>One document, PDF or text file.</summary>
    Document,

    /// <summary>One archive.</summary>
    Archive,

    /// <summary>One program, script or installer.</summary>
    Executable,

    /// <summary>One shortcut or link.</summary>
    Shortcut,

    /// <summary>One disk image.</summary>
    DiskImage,

    /// <summary>Any other single file.</summary>
    OtherFile,
}

/// <summary>
/// Chooses the middle zone of the command bar from the selection (spec §6.2). The table lists the actions the spec names for
/// each selection type; the bar shows only those whose command is registered, so the zone fills in as features land and never
/// offers a button that does nothing.
/// </summary>
public static class ContextActions
{
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv" };

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif", ".bmp", ".tif", ".tiff", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".svg", ".ico" };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac" };

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".rtf", ".odt", ".ods", ".csv", ".json", ".xml", ".log", ".html", ".htm" };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab" };

    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".bat", ".cmd", ".ps1", ".msi", ".msix", ".com", ".vbs", ".sh" };

    private static readonly HashSet<string> Shortcuts = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url" };

    private static readonly HashSet<string> DiskImages = new(StringComparer.OrdinalIgnoreCase) { ".iso", ".vhd", ".vhdx", ".img" };

    /// <summary>Sorts a selection into the types of spec §6.2.</summary>
    public static SelectionClass Classify(IReadOnlyList<FileEntry> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count == 0)
        {
            return SelectionClass.Nothing;
        }

        if (selected.Count > 1)
        {
            return SelectionClass.Several;
        }

        var entry = selected[0];
        if (Folders.FileColumns.IsFolderLike(entry))
        {
            return SelectionClass.Folder;
        }

        var extension = entry.Extension;
        return extension switch
        {
            _ when Videos.Contains(extension) => SelectionClass.Video,
            _ when Images.Contains(extension) => SelectionClass.Image,
            _ when Audio.Contains(extension) => SelectionClass.Audio,
            _ when Documents.Contains(extension) => SelectionClass.Document,
            _ when Archives.Contains(extension) => SelectionClass.Archive,
            _ when Executables.Contains(extension) => SelectionClass.Executable,
            _ when Shortcuts.Contains(extension) => SelectionClass.Shortcut,
            _ when DiskImages.Contains(extension) => SelectionClass.DiskImage,
            _ => SelectionClass.OtherFile,
        };
    }

    /// <summary>
    /// The actions the spec lists for <paramref name="selection"/>, in bar order. Commands that do not exist yet are in the
    /// list too (so the table matches the spec); <see cref="For"/> drops them.
    /// </summary>
    public static IReadOnlyList<BarButton> Wanted(SelectionClass selection) => selection switch
    {
        SelectionClass.Nothing =>
        [
            new(CommandIds.SelectAll, "Select all", "", "Ctrl+A"),
            new(CommandIds.SelectNone, "Select none", ""),
            new(CommandIds.SelectInvert, "Invert selection", ""),
            new(CommandIds.SelectByPattern, "Select by pattern", ""),
            new(CommandIds.TabNew, "New tab here", ""),
            new(CommandIds.TerminalHere, "Open terminal here", ""),
            new(CommandIds.SpaceMap, "Space map of this folder", ""),
        ],
        SelectionClass.Folder =>
        [
            new(CommandIds.TabOpenInNewTab, "Open in new tab", ""),
            new(CommandIds.OpenInSplitPane, "Open in split pane", ""),
            new(CommandIds.FavoriteAdd, "Add to favorites", ""),
            new(CommandIds.SpaceMap, "Space map", ""),
            new(CommandIds.Compress, "Compress", ""),
            new(CommandIds.Properties, "Properties", "", "Alt+Enter"),
        ],
        SelectionClass.Several =>
        [
            new(CommandIds.BulkRename, "Bulk rename", ""),
            new(CommandIds.Compress, "Compress", ""),
            new(CommandIds.ComputeSize, "Compute total size", ""),
            new(CommandIds.Compare, "Compare", ""),
            new(CommandIds.MoveToNewFolder, "Move to new folder", ""),
            new(CommandIds.ContactSheet, "Contact sheets of the videos…", ""),
            new(CommandIds.TagSelection, "Tag", ""),
        ],
        SelectionClass.Video =>
        [
            new(CommandIds.ItemOpen, "Play", "", "Enter"),
            new(CommandIds.TriageFromHere, "Triage mode from here", ""),
            new(CommandIds.ContactSheet, "Contact sheet", ""),
            new(CommandIds.ExtractFrame, "Extract frame", ""),
            new(CommandIds.ShowProbe, "Show ffprobe info", ""),
        ],
        SelectionClass.Image =>
        [
            new(CommandIds.ItemOpen, "Open", "", "Enter"),
            new(CommandIds.SetWallpaper, "Set as wallpaper", ""),
            new(CommandIds.RotateLeft, "Rotate left", ""),
            new(CommandIds.RotateRight, "Rotate right", ""),
            new(CommandIds.ConvertFormat, "Convert format", ""),
        ],
        SelectionClass.Audio =>
        [
            new(CommandIds.ItemOpen, "Play", "", "Enter"),
            new(CommandIds.ShowProbe, "Show tags", ""),
        ],
        SelectionClass.Document or SelectionClass.OtherFile =>
        [
            new(CommandIds.ItemOpen, "Open", "", "Enter"),
            new(CommandIds.OpenWith, "Open with…", ""),
            new(CommandIds.Print, "Print", ""),
            new(CommandIds.Preview, "Preview", ""),
        ],
        SelectionClass.Archive =>
        [
            new(CommandIds.ExtractHere, "Extract here", ""),
            new(CommandIds.ExtractToFolder, "Extract to folder", ""),
            new(CommandIds.ArchiveBrowse, "Browse as folder", ""),
            new(CommandIds.TestArchive, "Test archive", ""),
        ],
        SelectionClass.Executable =>
        [
            new(CommandIds.ItemOpen, "Run", "", "Enter"),
            new(CommandIds.RunAsAdministrator, "Run as administrator", ""),
            new(CommandIds.ShowChecksum, "Show checksum", ""),
        ],
        SelectionClass.Shortcut =>
        [
            new(CommandIds.ItemOpen, "Open", "", "Enter"),
            new(CommandIds.OpenTargetLocation, "Open target location", ""),
            new(CommandIds.EditTarget, "Edit target", ""),
        ],
        SelectionClass.DiskImage =>
        [
            new(CommandIds.Mount, "Mount", ""),
            new(CommandIds.Burn, "Burn", ""),
        ],
        _ => [],
    };

    /// <summary>
    /// What a context button hands its command: the selected entry for commands that act on an item, the folder's path for
    /// commands that open or pin a folder, and the current folder for "New tab here" and the terminal.
    /// </summary>
    public static CommandContext ContextOf(BarButton button, IReadOnlyList<FileEntry> selected, Core.Paths.LocationPath current)
    {
        ArgumentNullException.ThrowIfNull(button);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(current);
        var entry = selected.Count == 1 ? selected[0] : null;
        var id = button.Command;
        if (id == CommandIds.TabOpenInNewTab)
        {
            return new CommandContext(entry?.Location.Value, true);
        }

        if (id == CommandIds.FavoriteAdd)
        {
            return new CommandContext(entry?.Location.Value);
        }

        if (id == CommandIds.TabNew || id == CommandIds.TerminalHere)
        {
            return new CommandContext(current.Value);
        }

        if (id == CommandIds.SpaceMap)
        {
            // The map of the folder that is selected; with nothing or a file selected, of the folder shown.
            return new CommandContext(entry is { Kind: Leviathan.Core.Model.FileEntryKind.Folder } ? entry.Location.Value : current.Value);
        }

        return new CommandContext(entry?.Location.Value, entry);
    }

    /// <summary>The actions to show now: <see cref="Wanted"/> without the commands nobody has registered yet.</summary>
    public static IReadOnlyList<BarButton> For(SelectionClass selection, CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return [.. Wanted(selection).Where(b => registry.TryGet(b.Command, out _))];
    }
}
