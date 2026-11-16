using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Commands;

/// <summary>Stable ids of the window-level commands (spec §6, §17.3).</summary>
public static class CommandIds
{
    private static readonly Lazy<IReadOnlySet<CommandId>> AllIds = new(() => typeof(CommandIds)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(CommandId))
        .Select(f => (CommandId)f.GetValue(null)!)
        .Concat(Enumerable.Range(1, QuickTargets.Count).SelectMany(n => new[] { QuickTargets.MoveId(n), QuickTargets.CopyId(n), QuickTargets.SetId(n) }))
        .ToHashSet());

    /// <summary>Every id declared here, so a settings file can be checked without a window (the Keyboard page, spec 17.3).</summary>
    public static IReadOnlySet<CommandId> All => AllIds.Value;

    /// <summary>New tab (Ctrl+T). Target: a location; default Home.</summary>
    public static readonly CommandId TabNew = new("tab.new");

    /// <summary>New tab right after the tab (tab menu). Parameter: the tab; default the selected one.</summary>
    public static readonly CommandId TabNewRight = new("tab.newRight");

    /// <summary>New tab right before the tab (tab menu). Parameter: the tab; default the selected one.</summary>
    public static readonly CommandId TabNewLeft = new("tab.newLeft");

    /// <summary>Close the selected tab (Ctrl+W).</summary>
    public static readonly CommandId TabClose = new("tab.close");

    /// <summary>Reopen the last closed tab (Ctrl+Shift+T).</summary>
    public static readonly CommandId TabReopen = new("tab.reopen");

    /// <summary>Duplicate the selected tab (Ctrl+Shift+K).</summary>
    public static readonly CommandId TabDuplicate = new("tab.duplicate");

    /// <summary>Next tab (Ctrl+Tab).</summary>
    public static readonly CommandId TabNext = new("tab.next");

    /// <summary>Previous tab (Ctrl+Shift+Tab).</summary>
    public static readonly CommandId TabPrevious = new("tab.previous");

    /// <summary>Go to tab N (Ctrl+1..9). Parameter: the number.</summary>
    public static readonly CommandId TabSelect = new("tab.select");

    /// <summary>Pin or unpin a tab (tab menu). Parameter: the tab; default the selected one.</summary>
    public static readonly CommandId TabTogglePin = new("tab.togglePin");

    /// <summary>Lock or unlock a tab: a locked tab opens folders in a new tab instead of navigating.</summary>
    public static readonly CommandId TabToggleLock = new("tab.toggleLock");

    /// <summary>Rename a tab. Target: the new label; empty restores the folder name.</summary>
    public static readonly CommandId TabRename = new("tab.rename");

    /// <summary>Colour a tab. Target: <c>#RRGGBB</c>; empty removes the colour.</summary>
    public static readonly CommandId TabColor = new("tab.color");

    /// <summary>Close every other tab that is not pinned.</summary>
    public static readonly CommandId TabCloseOthers = new("tab.closeOthers");

    /// <summary>Close the unpinned tabs to the right.</summary>
    public static readonly CommandId TabCloseRight = new("tab.closeRight");

    /// <summary>Move the tab one place left (Ctrl+Shift+PgUp).</summary>
    public static readonly CommandId TabMoveLeft = new("tab.moveLeft");

    /// <summary>Move the tab one place right (Ctrl+Shift+PgDn).</summary>
    public static readonly CommandId TabMoveRight = new("tab.moveRight");

    /// <summary>Put a tab (Parameter, default the current one) in a new group; Target: the group's name (spec §4.1).</summary>
    public static readonly CommandId TabGroupNew = new("tab.group.new");

    /// <summary>Add a tab to a group. Parameter: <see cref="TabGroupAdd"/>.</summary>
    public static readonly CommandId TabGroupAdd = new("tab.group.add");

    /// <summary>Take a tab out of its group. Parameter: the tab, default the current one.</summary>
    public static readonly CommandId TabGroupRemove = new("tab.group.remove");

    /// <summary>Rename a group. Parameter: the <see cref="TabGroup"/>. Target: the name.</summary>
    public static readonly CommandId TabGroupRename = new("tab.group.rename");

    /// <summary>Change a group's colour. Parameter: the <see cref="TabGroup"/>. Target: <c>#RRGGBB</c>.</summary>
    public static readonly CommandId TabGroupColor = new("tab.group.color");

    /// <summary>Fold a group to its chip or open it. Parameter: the <see cref="TabGroup"/>. Target: "true", "false", or nothing to toggle.</summary>
    public static readonly CommandId TabGroupCollapse = new("tab.group.collapse");

    /// <summary>Dissolve a group, keeping its tabs. Parameter: the <see cref="TabGroup"/>.</summary>
    public static readonly CommandId TabGroupUngroup = new("tab.group.ungroup");

    /// <summary>Close every tab of a group. Parameter: the <see cref="TabGroup"/>.</summary>
    public static readonly CommandId TabGroupClose = new("tab.group.close");

    /// <summary>Open a new tab at the end of a group. Parameter: the <see cref="TabGroup"/>. Target: the location, default Home.</summary>
    public static readonly CommandId TabGroupNewTab = new("tab.group.newTab");

    /// <summary>Move a group's tabs to a new window. Parameter: the <see cref="TabGroup"/>.</summary>
    public static readonly CommandId TabGroupToWindow = new("tab.group.toWindow");

    /// <summary>Move a whole group within the strip (drag its chip). Parameter: <see cref="TabGroupMove"/>.</summary>
    public static readonly CommandId TabGroupMove = new("tab.group.move");

    /// <summary>Drag a group out of the strip into a new window. Parameter: <see cref="TabGroupTearOff"/>.</summary>
    public static readonly CommandId TabGroupTearOff = new("tab.group.tearoff");

    /// <summary>Drop a group from this or another window onto this strip. Parameter: <see cref="TabGroupDrop"/>.</summary>
    public static readonly CommandId TabGroupDrop = new("tab.group.drop");

    /// <summary>Move a tab to an index (drag within the strip). Parameter: <see cref="TabMove"/>.</summary>
    public static readonly CommandId TabMove = new("tab.move");

    /// <summary>Drag a tab out of the strip into a new window. Parameter: <see cref="TabTearOff"/>.</summary>
    public static readonly CommandId TabTearOff = new("tab.tearoff");

    /// <summary>Drop a tab from this or another window onto this strip. Parameter: <see cref="TabDrop"/>.</summary>
    public static readonly CommandId TabDrop = new("tab.drop");

    /// <summary>Open a folder in a new tab, in the background unless the parameter is true. Target: the location.</summary>
    public static readonly CommandId TabOpenInNewTab = new("tab.openInNewTab");

    /// <summary>New window (Ctrl+N).</summary>
    public static readonly CommandId WindowNew = new("window.new");

    /// <summary>Back (Alt+Left). Parameter: number of steps, default 1.</summary>
    public static readonly CommandId NavBack = new("nav.back");

    /// <summary>Forward (Alt+Right). Parameter: number of steps, default 1.</summary>
    public static readonly CommandId NavForward = new("nav.forward");

    /// <summary>Up (Alt+Up).</summary>
    public static readonly CommandId NavUp = new("nav.up");

    /// <summary>Reload (F5).</summary>
    public static readonly CommandId NavReload = new("nav.reload");

    /// <summary>Go to what was typed in the address bar. Target: the text.</summary>
    public static readonly CommandId NavGo = new("nav.go");

    /// <summary>Open an item: folders navigate, files open with their default app. Parameter: the entry.</summary>
    public static readonly CommandId ItemOpen = new("item.open");

    /// <summary>Switch the file view layout. Parameter: a <see cref="ViewLayout"/>.</summary>
    public static readonly CommandId ViewLayout = new("view.layout");

    /// <summary>Details layout (Ctrl+Shift+6).</summary>
    public static readonly CommandId ViewDetails = new("view.details");

    /// <summary>List layout (Ctrl+Shift+5).</summary>
    public static readonly CommandId ViewList = new("view.list");

    /// <summary>Thumbnails layout (Ctrl+Shift+2).</summary>
    public static readonly CommandId ViewThumbnails = new("view.thumbnails");

    /// <summary>Tiles layout.</summary>
    public static readonly CommandId ViewTiles = new("view.tiles");

    /// <summary>Gallery layout.</summary>
    public static readonly CommandId ViewGallery = new("view.gallery");

    /// <summary>Resize thumbnails (Ctrl+mouse wheel). Parameter: wheel notches, positive = bigger.</summary>
    public static readonly CommandId ViewZoom = new("view.zoom");

    /// <summary>Set the thumbnail edge in pixels (the status bar slider). Parameter: the size.</summary>
    public static readonly CommandId ViewThumbnailSize = new("view.thumbnailSize");

    /// <summary>Pin or unpin the current folder in Favorites (Ctrl+D).</summary>
    public static readonly CommandId FavoriteToggle = new("favorite.toggle");

    /// <summary>Select every row (Ctrl+A).</summary>
    public static readonly CommandId SelectAll = new("select.all");

    /// <summary>Clear the selection.</summary>
    public static readonly CommandId SelectNone = new("select.none");

    /// <summary>Select what is not selected.</summary>
    public static readonly CommandId SelectInvert = new("select.invert");

    /// <summary>Select the rows matching a pattern such as <c>*.mkv</c>. Target: the pattern.</summary>
    public static readonly CommandId SelectByPattern = new("select.pattern");

    /// <summary>Create a folder in the current folder (Ctrl+Shift+N).</summary>
    public static readonly CommandId NewFolder = new("new.folder");

    /// <summary>Create the New menu item with the id in Target (spec §6.1).</summary>
    public static readonly CommandId NewItem = new("new.item");

    /// <summary>Open a terminal in the current folder.</summary>
    public static readonly CommandId TerminalHere = new("terminal.here");

    /// <summary>Pin the folder in Target, or the selected folder, to Favorites.</summary>
    public static readonly CommandId FavoriteAdd = new("favorite.add");

    /// <summary>Hide or show the drive summary card of the drive root in Target.</summary>
    public static readonly CommandId DriveCardToggle = new("drive.card");

    /// <summary>Drop items on a folder. Target: the folder; parameter: <see cref="ItemDrop"/>.</summary>
    public static readonly CommandId DropItems = new("item.drop");

    /// <summary>Sort by the column in Target; sorting by the column already in use flips the direction.</summary>
    public static readonly CommandId SortBy = new("sort.by");

    /// <summary>Set the sort direction. Target: <c>asc</c> or <c>desc</c>.</summary>
    public static readonly CommandId SortDirection = new("sort.direction");

    /// <summary>Group by the column in Target; empty ungroups.</summary>
    public static readonly CommandId GroupBy = new("group.by");

    /// <summary>Where folders sort. Target: <c>First</c>, <c>Mixed</c> or <c>Last</c>.</summary>
    public static readonly CommandId FolderPlacement = new("sort.folders");

    /// <summary>Filter the folder as you type. Target: the filter text; empty clears it.</summary>
    public static readonly CommandId FilterSet = new("filter.set");

    /// <summary>Focus the filter box (Ctrl+Shift+F).</summary>
    public static readonly CommandId FilterFocus = new("filter.focus");

    /// <summary>Focuses the search box (Ctrl+F).</summary>
    public static readonly CommandId SearchFocus = new("search.focus");

    /// <summary>Keeps the folder shown in the search index, or stops (spec §13: the NAS index).</summary>
    public static readonly CommandId SearchIndexToggle = new("search.index");

    /// <summary>Pins the search results shown to Favorites, where it runs again each time it is opened (spec §13.2).</summary>
    public static readonly CommandId SearchSave = new("search.save");

    /// <summary>Searches the folder shown and everything below it; the target is the query (spec §13).</summary>
    public static readonly CommandId SearchRun = new("search.run");

    /// <summary>Show or hide hidden and system items.</summary>
    public static readonly CommandId ViewHidden = new("view.hidden");

    /// <summary>Give a favorite its own name. Target: the folder. Parameter: the name, or null for the folder's own (spec §5.2).</summary>
    public static readonly CommandId FavoriteRename = new("favorite.rename");

    /// <summary>Change a favorite's icon and colour. Target: the folder. Parameter: a <see cref="Sidebar.FavoriteStyleChange"/> (spec §5.2).</summary>
    public static readonly CommandId FavoriteStyle = new("favorite.style");

    /// <summary>Set the Details columns. Parameter: a <see cref="Folders.ColumnChoice"/> from the column chooser (spec §8.2).</summary>
    public static readonly CommandId ViewColumns = new("view.columns");

    /// <summary>Open or close the status hub (Ctrl+Shift+O). Parameter: the <see cref="Hub.HubTab"/> to show, or none for the one last used.</summary>
    public static readonly CommandId HubToggle = new("hub.toggle");

    /// <summary>Open or close the settings sheet (Ctrl+,). Parameter: the page id to open on.</summary>
    public static readonly CommandId SettingsOpen = new("settings.open");

    /// <summary>Collapse or restore the sidebar (Ctrl+B).</summary>
    public static readonly CommandId ViewSidebar = new("pane.sidebar");

    /// <summary>Show or hide the preview pane (Alt+P).</summary>
    public static readonly CommandId PreviewPane = new("pane.preview");

    /// <summary>The large floating preview of the selection (Space); the same key closes it (spec §9).</summary>
    public static readonly CommandId PreviewQuick = new("pane.previewQuick");

    /// <summary>Small video in the preview pane (the S button).</summary>
    public static readonly CommandId PreviewVideoSmall = new("pane.previewVideoSmall");

    /// <summary>Medium video in the preview pane (the M button).</summary>
    public static readonly CommandId PreviewVideoMedium = new("pane.previewVideoMedium");

    /// <summary>Large video in the preview pane, filling its width (the L button).</summary>
    public static readonly CommandId PreviewVideoLarge = new("pane.previewVideoLarge");

    /// <summary>One pane: closes a split and keeps the focused pane (spec §15.1).</summary>
    public static readonly CommandId PaneSingle = new("pane.single");

    /// <summary>Two panes side by side (Ctrl+Backslash); again closes the split.</summary>
    public static readonly CommandId PaneSideBySide = new("pane.sideBySide");

    /// <summary>Two panes, one above the other (Ctrl+Shift+Backslash); again closes the split.</summary>
    public static readonly CommandId PaneTopBottom = new("pane.topBottom");

    /// <summary>Copy the selection to the folder of the other pane (F5 while split).</summary>
    public static readonly CommandId PaneCopyToOther = new("pane.copyToOther");

    /// <summary>Move the selection to the folder of the other pane (F6 while split).</summary>
    public static readonly CommandId PaneMoveToOther = new("pane.moveToOther");

    /// <summary>Swap what the two panes show (Ctrl+Shift+S).</summary>
    public static readonly CommandId PaneSwap = new("pane.swap");

    /// <summary>Show the focused pane's folder in the other pane too (Alt+=).</summary>
    public static readonly CommandId PaneSameFolder = new("pane.sameFolder");

    /// <summary>Run the selected program elevated. Only that program is elevated (spec §15.6).</summary>
    public static readonly CommandId RunAsAdministrator = new("item.runAsAdmin");

    /// <summary>Choose the app to open the selected file with.</summary>
    public static readonly CommandId OpenWith = new("item.openWith");

    /// <summary>Show the Windows Properties dialog for the selected item (Alt+Enter).</summary>
    public static readonly CommandId Properties = new("item.properties");

    // Ids of commands that arrive in later slices and phases. They are declared now so the context-action table of spec §6.2 and
    // the clipboard buttons name them; a button whose command is not registered is disabled or left out.

    /// <summary>Cut (Ctrl+X): the selection goes on the clipboard; paste moves it.</summary>
    public static readonly CommandId EditCut = new("edit.cut");

    /// <summary>Copy (Ctrl+C): the selection goes on the clipboard; paste copies it.</summary>
    public static readonly CommandId EditCopy = new("edit.copy");

    /// <summary>Paste (Ctrl+V): the clipboard's files go into the current folder.</summary>
    public static readonly CommandId EditPaste = new("edit.paste");

    /// <summary>Paste into a new folder created in the current folder, which then opens its rename box.</summary>
    public static readonly CommandId EditPasteIntoNewFolder = new("edit.pasteIntoNewFolder");

    /// <summary>Rename (F2): shows the inline rename box on the selected item.</summary>
    public static readonly CommandId EditRename = new("edit.rename");

    /// <summary>Finish an inline rename. Target: the new name. Parameter: true to go on to the next item (Tab).</summary>
    public static readonly CommandId RenameCommit = new("edit.renameCommit");

    /// <summary>Delete (Del): the selection goes to the Recycle Bin, or to the share's recycle folder on a network share.</summary>
    public static readonly CommandId EditDelete = new("edit.delete");

    /// <summary>Delete permanently (Shift+Del), after the user confirms.</summary>
    public static readonly CommandId EditDeletePermanent = new("edit.deletePermanent");

    /// <summary>Undo (Ctrl+Z): the last rename or delete, from any window.</summary>
    public static readonly CommandId EditUndo = new("edit.undo");

    /// <summary>Redo (Ctrl+Y or Ctrl+Shift+Z): what was undone.</summary>
    public static readonly CommandId EditRedo = new("edit.redo");

    /// <summary>Save the open windows as a named profile. Target: the name (spec §17.1).</summary>
    public static readonly CommandId ProfileSave = new("profile.save");

    /// <summary>Open a named profile. Target: the name. Parameter: false to add its windows to the open ones instead of replacing them.</summary>
    public static readonly CommandId ProfileOpen = new("profile.open");

    /// <summary>Delete a named profile. Target: the name.</summary>
    public static readonly CommandId ProfileDelete = new("profile.delete");

    /// <summary>Space map of a folder: the target names it, else the selected folder, else the folder shown (Ctrl+Shift+M, spec §14).</summary>
    public static readonly CommandId SpaceMap = new("spacemap.open");

    /// <summary>Open a folder in the second pane (Phase 2).</summary>
    public static readonly CommandId OpenInSplitPane = new("pane.openInSplit");

    /// <summary>Compress to an archive (Phase 2).</summary>
    public static readonly CommandId Compress = new("archive.compress");

    /// <summary>Bulk rename of the selected items (spec §15.3); F2 with several items selected does the same.</summary>
    public static readonly CommandId BulkRename = new("edit.bulkRename");

    /// <summary>Compute the total size of the selection (Phase 2).</summary>
    public static readonly CommandId ComputeSize = new("item.computeSize");

    /// <summary>Compare two items (Phase 2).</summary>
    public static readonly CommandId Compare = new("item.compare");

    /// <summary>Move the selection into a new folder (Phase 2).</summary>
    public static readonly CommandId MoveToNewFolder = new("edit.moveToNewFolder");

    /// <summary>Tag the selection (Phase 3).</summary>
    public static readonly CommandId TagSelection = new("tag.apply");

    /// <summary>Triage mode starting at the selected video (Phase 3).</summary>
    public static readonly CommandId TriageFromHere = new("triage.fromHere");

    /// <summary>Triage a saved inbox in sessions of N (Phase 3, E5).</summary>
    public static readonly CommandId TriageInbox = new("triage.inbox");

    /// <summary>Find duplicate files by name in this folder.</summary>
    public static readonly CommandId FindDuplicates = new("tools.duplicates");

    /// <summary>Find duplicate files by contents under the current folder (spec §15.2).</summary>
    public static readonly CommandId FindDuplicateFiles = new("tools.duplicateFiles");

    /// <summary>Contact sheet of one video (a window with the grid, saved on request), or of every video in a selection (made into a folder in the background).</summary>
    public static readonly CommandId ContactSheet = new("media.contactSheet");

    /// <summary>Extract a frame from a video (Phase 3).</summary>
    public static readonly CommandId ExtractFrame = new("media.extractFrame");

    /// <summary>Show ffprobe info or audio tags (Phase 3).</summary>
    public static readonly CommandId ShowProbe = new("media.probe");

    /// <summary>Set an image as the wallpaper (Phase 3).</summary>
    public static readonly CommandId SetWallpaper = new("image.wallpaper");

    /// <summary>Rotate an image left (Phase 3).</summary>
    public static readonly CommandId RotateLeft = new("image.rotateLeft");

    /// <summary>Rotate an image right (Phase 3).</summary>
    public static readonly CommandId RotateRight = new("image.rotateRight");

    /// <summary>Convert an image format (Phase 3).</summary>
    public static readonly CommandId ConvertFormat = new("image.convert");

    /// <summary>Print the selected document.</summary>
    public static readonly CommandId Print = new("item.print");

    /// <summary>Preview the selected item in the preview pane (Phase 3).</summary>
    public static readonly CommandId Preview = new("item.preview");

    /// <summary>Extract an archive to a folder the user picks (Phase 5).</summary>
    public static readonly CommandId ExtractTo = new("archive.extractTo");

    /// <summary>The command palette: every command with its shortcut (Ctrl+Shift+P).</summary>
    public static readonly CommandId PaletteCommands = new("palette.commands");

    /// <summary>The go-to palette: favorites, recent folders, open tabs and commands (Ctrl+P).</summary>
    public static readonly CommandId PaletteGoTo = new("palette.goTo");

    /// <summary>Compare two folders and bring them into line (Phase 5).</summary>
    public static readonly CommandId SyncFolders = new("folder.sync");

    /// <summary>Compress with a chosen format, level and password (Phase 5).</summary>
    public static readonly CommandId CompressWith = new("archive.compressWith");

    /// <summary>Extract an archive next to itself (Phase 2).</summary>
    public static readonly CommandId ExtractHere = new("archive.extractHere");

    /// <summary>Extract an archive into a folder of its name (Phase 2).</summary>
    public static readonly CommandId ExtractToFolder = new("archive.extractToFolder");

    /// <summary>Browse an archive as a folder (Phase 2).</summary>
    public static readonly CommandId ArchiveBrowse = new("archive.browse");

    /// <summary>Test an archive (Phase 2).</summary>
    public static readonly CommandId TestArchive = new("archive.test");

    /// <summary>Show a file's checksum (Phase 2).</summary>
    public static readonly CommandId ShowChecksum = new("item.checksum");

    /// <summary>Open the folder a shortcut points to.</summary>
    public static readonly CommandId OpenTargetLocation = new("item.openTargetLocation");

    /// <summary>Edit a shortcut's target.</summary>
    public static readonly CommandId EditTarget = new("item.editTarget");

    /// <summary>Mount a disk image.</summary>
    public static readonly CommandId Mount = new("item.mount");

    /// <summary>Burn a disk image.</summary>
    public static readonly CommandId Burn = new("item.burn");

    /// <summary>Focus the address bar (Ctrl+L).</summary>
    public static readonly CommandId AddressFocus = new("address.focus");

    /// <summary>Open the diagnostics window.</summary>
    public static readonly CommandId AppDiagnostics = new("app.diagnostics");
}
