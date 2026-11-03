// View models are UI-thread affine: everything a control can observe is changed on the thread that started the command, so the
// awaits in this file deliberately resume on the caller's context (crash signature 52a0ec04c34d: RPC_E_WRONG_THREAD from a
// continuation that ran on the thread pool). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using System.Text.RegularExpressions;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Diagnostics;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands;

/// <summary>What a window's commands need from the UI layer that view models cannot do themselves.</summary>
public interface IShellActions
{
    /// <summary>Moves keyboard focus into the address bar and selects its text (Ctrl+L).</summary>
    void FocusAddressBar();

    /// <summary>Opens the diagnostics window (worker connection, job queue, crash reports).</summary>
    void OpenDiagnostics();

    /// <summary>Moves keyboard focus into the filter box of the command bar (Ctrl+Shift+F).</summary>
    void FocusFilterBox();

    /// <summary>Shows a short message in the status bar, e.g. why an address could not be opened.</summary>
    void ShowMessage(string message);

    /// <summary>Asks a yes/no question (for example before deleting for good) and returns true when the user confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

/// <summary>Stable ids of the window-level commands (spec §6, §17.3).</summary>
public static class CommandIds
{
    private static readonly Lazy<IReadOnlySet<CommandId>> AllIds = new(() => typeof(CommandIds)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(CommandId))
        .Select(f => (CommandId)f.GetValue(null)!)
        .ToHashSet());

    /// <summary>Every id declared here, so a settings file can be checked without a window (the Keyboard page, spec 17.3).</summary>
    public static IReadOnlySet<CommandId> All => AllIds.Value;

    /// <summary>New tab (Ctrl+T). Target: a location; default Home.</summary>
    public static readonly CommandId TabNew = new("tab.new");

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

    /// <summary>Space map of a folder (Phase 5).</summary>
    public static readonly CommandId SpaceMap = new("spacemap.open");

    /// <summary>Open a folder in the second pane (Phase 2).</summary>
    public static readonly CommandId OpenInSplitPane = new("pane.openInSplit");

    /// <summary>Compress to an archive (Phase 2).</summary>
    public static readonly CommandId Compress = new("archive.compress");

    /// <summary>Bulk rename (Phase 2).</summary>
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

    /// <summary>Contact sheet of a video (Phase 3).</summary>
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

/// <summary>Parameter of <see cref="CommandIds.TabMove"/>.</summary>
/// <param name="Tab">The tab dragged.</param>
/// <param name="Index">Where it landed.</param>
public sealed record TabMove(TabViewModel Tab, int Index);

/// <summary>Parameter of <see cref="CommandIds.TabTearOff"/>.</summary>
/// <param name="Tab">The tab dragged out.</param>
/// <param name="ScreenX">Drop point in screen pixels.</param>
/// <param name="ScreenY">Drop point in screen pixels.</param>
public sealed record TabTearOff(TabViewModel Tab, int ScreenX, int ScreenY);

/// <summary>Parameter of <see cref="CommandIds.TabGroupAdd"/>.</summary>
/// <param name="Tab">The tab to add.</param>
/// <param name="Group">The group to add it to.</param>
/// <param name="Position">Where among the group's tabs, or null for the end.</param>
public sealed record TabGroupAdd(TabViewModel Tab, TabGroup Group, int? Position = null);

/// <summary>Parameter of <see cref="CommandIds.TabGroupMove"/>.</summary>
/// <param name="Group">The group dragged.</param>
/// <param name="Index">Where its first tab should land among the other tabs.</param>
public sealed record TabGroupMove(TabGroup Group, int Index);

/// <summary>Parameter of <see cref="CommandIds.TabGroupTearOff"/>.</summary>
/// <param name="Group">The group dragged out.</param>
/// <param name="ScreenX">Drop point in screen pixels.</param>
/// <param name="ScreenY">Drop point in screen pixels.</param>
public sealed record TabGroupTearOff(TabGroup Group, int ScreenX, int ScreenY);

/// <summary>Parameter of <see cref="CommandIds.TabGroupDrop"/>.</summary>
/// <param name="Group">The group dropped, from this or another window.</param>
/// <param name="Index">Where in the strip, or null for the end.</param>
public sealed record TabGroupDrop(TabGroup Group, int? Index);

/// <summary>Parameter of <see cref="CommandIds.DropItems"/>.</summary>
/// <param name="Sources">The items dropped.</param>
/// <param name="Modifiers">The modifier keys held at the drop.</param>
public sealed record ItemDrop(IReadOnlyList<LocationPath> Sources, Transfers.DropModifiers Modifiers);

/// <summary>Parameter of <see cref="CommandIds.TabDrop"/>.</summary>
/// <param name="Tab">The tab dropped.</param>
/// <param name="Index">Where in the strip, or null for the end.</param>
public sealed record TabDrop(TabViewModel Tab, int? Index);

/// <summary>
/// Registers the commands of one window against its tabs (build plan, Phase 1: every user action is a command). The UI
/// buttons, menus, shortcuts and, later, the command palette all call these, so the action trail sees everything the user does.
/// </summary>
public static partial class WindowCommands
{
    /// <summary>Creates the registry for <paramref name="window"/>.</summary>
    public static CommandRegistry Create(
        ActionTrail trail,
        WindowViewModel window,
        WindowManager windows,
        AddressResolver resolver,
        IFileLauncher launcher,
        IShellActions shell,
        Func<LocationPath> home,
        Sidebar.FavoritesList? favorites = null,
        NewMenu.NewItemService? newItems = null,
        Views.ViewOptions? viewOptions = null,
        Folders.RenameService? renamer = null,
        Transfers.ITransferService? transfers = null,
        Drives.DriveCardPreferences? driveCards = null,
        Transfers.IDeleteService? deleter = null,
        Undo.UndoService? undo = null,
        Transfers.IFileClipboard? clipboard = null,
        Sessions.ProfileService? profiles = null)
    {
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(home);

        var registry = new CommandRegistry(trail);
        var strip = window.Tabs;

        // A locked tab never navigates: its folder opens in a new tab (spec 4.1). Hooks follow tabs as they enter and leave this window.
        var hooked = new Dictionary<TabViewModel, EventHandler<LocationPath>>();
        void Hook(TabViewModel tab)
        {
            if (hooked.ContainsKey(tab))
            {
                return;
            }

            void OnRequested(object? sender, LocationPath location) =>
                _ = ReportFailures(registry.ExecuteAsync(CommandIds.TabNew, new CommandContext(location.Value), CancellationToken.None), shell);
            hooked[tab] = OnRequested;
            tab.OpenInNewTabRequested += OnRequested;
        }

        foreach (var existing in strip.Tabs)
        {
            Hook(existing);
        }

        strip.Tabs.CollectionChanged += (_, e) =>
        {
            foreach (var tab in e.NewItems?.OfType<TabViewModel>() ?? [])
            {
                Hook(tab);
            }

            foreach (var tab in e.OldItems?.OfType<TabViewModel>() ?? [])
            {
                if (hooked.Remove(tab, out var handler))
                {
                    tab.OpenInNewTabRequested -= handler;
                }
            }
        };

        TabViewModel? Current() => strip.Selected;
        TabViewModel? TabOf(CommandContext c) => c.Parameter as TabViewModel ?? Current();

        void Add(CommandId id, string title, string category, Func<CommandContext, CancellationToken, Task> run, Func<CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
            registry.Register(new AppCommand(new CommandDefinition(id, title, category, icon, shortcut), run, can));

        static int Steps(CommandContext context) => context.Parameter is int steps and > 0 ? steps : 1;

        Add(CommandIds.TabNew, "New tab", "Tabs", async (c, ct) =>
        {
            var location = LocationPath.TryParse(c.Target, out var parsed) ? parsed : home();
            var tab = strip.NewTab(location);
            await tab.ReloadAsync(ct);
        }, shortcut: "Ctrl+T", icon: "Add");

        Add(CommandIds.TabClose, "Close tab", "Tabs", (c, _) =>
        {
            if (TabOf(c) is { } tab)
            {
                strip.Close(tab);
            }

            return Task.CompletedTask;
        }, c => TabOf(c) is { IsPinned: false }, shortcut: "Ctrl+W");

        Add(CommandIds.TabReopen, "Reopen closed tab", "Tabs", async (_, ct) =>
        {
            if (strip.Reopen() is { } tab)
            {
                await tab.ReloadAsync(ct);
            }
        }, _ => strip.CanReopen, shortcut: "Ctrl+Shift+T");

        Add(CommandIds.TabDuplicate, "Duplicate tab", "Tabs", async (c, ct) =>
        {
            if (TabOf(c) is { } tab)
            {
                await strip.Duplicate(tab).ReloadAsync(ct);
            }
        }, c => TabOf(c) is not null, shortcut: "Ctrl+Shift+K");

        Add(CommandIds.TabNext, "Next tab", "Tabs", (_, _) => Run(() => strip.Cycle(forward: true)), _ => strip.Tabs.Count > 1, shortcut: "Ctrl+Tab");
        Add(CommandIds.TabPrevious, "Previous tab", "Tabs", (_, _) => Run(() => strip.Cycle(forward: false)), _ => strip.Tabs.Count > 1, shortcut: "Ctrl+Shift+Tab");
        Add(CommandIds.TabSelect, "Go to tab", "Tabs", (c, _) => Run(() => strip.SelectByNumber(c.Parameter is int n ? n : 0)), c => c.Parameter is int);

        Add(CommandIds.TabTogglePin, "Pin tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                strip.SetPinned(tab, !tab.IsPinned);
            }
        }), c => TabOf(c) is not null);

        Add(CommandIds.TabToggleLock, "Lock tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                tab.IsLocked = !tab.IsLocked;
            }
        }), c => TabOf(c) is not null);

        Add(CommandIds.TabRename, "Rename tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                var label = c.Target?.Trim();
                tab.CustomTitle = string.IsNullOrEmpty(label) ? null : label.Length > MaxTabLabel ? label[..MaxTabLabel] : label;
            }
        }), c => TabOf(c) is not null);

        Add(CommandIds.TabColor, "Colour tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is not { } tab)
            {
                return;
            }

            var value = c.Target?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                tab.Accent = null;
            }
            else if (HexColor().IsMatch(value))
            {
                tab.Accent = value.ToUpperInvariant();
            }
            else
            {
                shell.ShowMessage($"'{value}' is not a colour. Use the form #RRGGBB.");
            }
        }), c => TabOf(c) is not null);

        Add(CommandIds.TabCloseOthers, "Close other tabs", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                strip.CloseOthers(tab);
            }
        }), c => TabOf(c) is { } tab && strip.Tabs.Any(t => t != tab && !t.IsPinned));

        Add(CommandIds.TabCloseRight, "Close tabs to the right", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { } tab)
            {
                strip.CloseToRight(tab);
            }
        }), c => TabOf(c) is { } tab && strip.Tabs.Skip(strip.Tabs.IndexOf(tab) + 1).Any(t => !t.IsPinned));

        Add(CommandIds.TabMoveLeft, "Move tab left", "Tabs", (c, _) => Run(() => strip.Nudge(TabOf(c)!, -1)), c => TabOf(c) is not null, shortcut: "Ctrl+Shift+PageUp");
        Add(CommandIds.TabMoveRight, "Move tab right", "Tabs", (c, _) => Run(() => strip.Nudge(TabOf(c)!, 1)), c => TabOf(c) is not null, shortcut: "Ctrl+Shift+PageDown");
        Add(CommandIds.TabMove, "Move tab", "Tabs", (c, _) => Run(() => strip.Move(((TabMove)c.Parameter!).Tab, ((TabMove)c.Parameter!).Index)), c => c.Parameter is TabMove);

        // Tab groups (spec §4.1, D32). Parameters are the strip's own objects, so a group menu or a drag hands them straight over.
        TabGroup? GroupOfParameter(CommandContext c) => c.Parameter is TabGroup g && strip.Groups.Contains(g) ? g : null;

        Add(CommandIds.TabGroupNew, "Add tab to new group", "Tabs", (c, _) => Run(() => strip.CreateGroup([TabOf(c)!], c.Target)), c => TabOf(c) is { IsPinned: false });
        Add(CommandIds.TabGroupAdd, "Add tab to group", "Tabs", (c, _) => Run(() =>
        {
            var add = (TabGroupAdd)c.Parameter!;
            strip.AddToGroup(add.Tab, add.Group, add.Position);
        }), c => c.Parameter is TabGroupAdd { Tab.IsPinned: false } add && strip.Groups.Contains(add.Group) && strip.Tabs.Contains(add.Tab));
        Add(CommandIds.TabGroupRemove, "Remove tab from group", "Tabs", (c, _) => Run(() => strip.RemoveFromGroup(TabOf(c)!)), c => TabOf(c)?.GroupId is not null);
        Add(CommandIds.TabGroupRename, "Rename group", "Tabs", (c, _) => Run(() => GroupOfParameter(c)!.Name = (c.Target ?? string.Empty).Trim()), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupColor, "Change group colour", "Tabs", (c, _) => Run(() => GroupOfParameter(c)!.Color = c.Target!.ToUpperInvariant()),
            c => GroupOfParameter(c) is not null && TabGroupColors.IsColor(c.Target));
        Add(CommandIds.TabGroupCollapse, "Collapse or expand group", "Tabs", (c, _) => Run(() =>
        {
            var group = GroupOfParameter(c)!;
            var collapse = c.Target is null ? !group.IsCollapsed : !c.Target.Equals("false", StringComparison.OrdinalIgnoreCase);
            if (!strip.SetCollapsed(group, collapse))
            {
                shell.ShowMessage("The group holds every tab in this window, so it cannot be folded away.");
            }
        }), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupUngroup, "Ungroup", "Tabs", (c, _) => Run(() => strip.Ungroup(GroupOfParameter(c)!)), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupClose, "Close group", "Tabs", (c, _) => Run(() => strip.CloseGroup(GroupOfParameter(c)!)), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupNewTab, "New tab in group", "Tabs", async (c, ct) =>
        {
            var tab = strip.NewTabInGroup(GroupOfParameter(c)!, LocationPath.TryParse(c.Target, out var parsed) ? parsed : null);
            if (tab is not null)
            {
                await tab.ReloadAsync(ct);
            }
        }, c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupToWindow, "Move group to new window", "Tabs", (c, _) => Run(() =>
        {
            var bounds = window.Bounds.Rect;
            if (windows.TearOffGroup(window, GroupOfParameter(c)!, bounds.X + 48, bounds.Y + 48) is null)
            {
                shell.ShowMessage("The group holds every tab in this window, so there is nothing to move.");
            }
        }), c => GroupOfParameter(c) is not null);
        Add(CommandIds.TabGroupMove, "Move group", "Tabs", (c, _) => Run(() =>
        {
            var move = (TabGroupMove)c.Parameter!;
            strip.MoveGroup(move.Group, move.Index);
        }), c => c.Parameter is TabGroupMove move && strip.Groups.Contains(move.Group));
        Add(CommandIds.TabGroupTearOff, "Move group to a new window", "Tabs", (c, _) => Run(() =>
        {
            var drag = (TabGroupTearOff)c.Parameter!;
            windows.TearOffGroup(window, drag.Group, drag.ScreenX, drag.ScreenY);
        }), c => c.Parameter is TabGroupTearOff);
        Add(CommandIds.TabGroupDrop, "Drop group here", "Tabs", (c, _) => Run(() =>
        {
            var drop = (TabGroupDrop)c.Parameter!;
            var from = windows.Windows.FirstOrDefault(w => w.Tabs.Groups.Contains(drop.Group));
            if (from == window)
            {
                strip.MoveGroup(drop.Group, drop.Index ?? strip.Tabs.Count);
            }
            else if (from is not null)
            {
                windows.MergeGroup(from, drop.Group, window, drop.Index);
            }
        }), c => c.Parameter is TabGroupDrop);

        Add(CommandIds.TabTearOff, "Move tab to a new window", "Tabs", (c, _) => Run(() =>
        {
            var drag = (TabTearOff)c.Parameter!;
            windows.TearOff(window, drag.Tab, drag.ScreenX, drag.ScreenY);
        }), c => c.Parameter is TabTearOff);

        Add(CommandIds.TabDrop, "Drop tab here", "Tabs", (c, _) => Run(() =>
        {
            var drop = (TabDrop)c.Parameter!;
            var from = windows.FindWindowOf(drop.Tab);
            if (from is null)
            {
                return;
            }

            if (from == window)
            {
                strip.Move(drop.Tab, drop.Index ?? strip.Tabs.Count - 1);
            }
            else
            {
                windows.Merge(from, drop.Tab, window, drop.Index);
            }
        }), c => c.Parameter is TabDrop);

        Add(CommandIds.TabOpenInNewTab, "Open in new tab", "Tabs", async (c, ct) =>
        {
            if (!LocationPath.TryParse(c.Target, out var location))
            {
                return;
            }

            var tab = strip.NewTab(location, select: c.Parameter is true);
            await tab.ReloadAsync(ct);
        }, c => !string.IsNullOrWhiteSpace(c.Target));

        Add(CommandIds.WindowNew, "New window", "Windows", (c, _) =>
        {
            windows.OpenWindow(LocationPath.TryParse(c.Target, out var parsed) ? parsed : home());
            return Task.CompletedTask;
        }, shortcut: "Ctrl+N", icon: "NewWindow");

        // Startup profiles (spec §17.1). Saving and opening are the user's own actions, so a refusal says why in the status bar.
        async Task ReportProfile(Func<Task<string>> action)
        {
            try
            {
                shell.ShowMessage(await action());
            }
            catch (Sessions.ProfileException ex)
            {
                shell.ShowMessage(ex.Message);
            }
        }

        Add(CommandIds.ProfileSave, "Save current windows as profile", "Profiles", (c, ct) => ReportProfile(async () =>
        {
            await profiles!.SaveCurrentAsync(c.Target!, ct);
            return $"Saved the open windows as the profile \"{c.Target!.Trim()}\".";
        }), c => profiles is not null && !string.IsNullOrWhiteSpace(c.Target));
        Add(CommandIds.ProfileOpen, "Open profile", "Profiles", (c, _) => ReportProfile(() =>
        {
            var opened = profiles!.Open(c.Target!, replace: c.Parameter is not false);
            return Task.FromResult($"Opened the profile \"{c.Target!.Trim()}\" ({opened} window{(opened == 1 ? string.Empty : "s")}).");
        }), c => profiles is not null && profiles.Catalog.Find(c.Target) is not null);
        Add(CommandIds.ProfileDelete, "Delete profile", "Profiles", (c, ct) => ReportProfile(async () =>
            await profiles!.DeleteAsync(c.Target!, ct) ? $"Deleted the profile \"{c.Target!.Trim()}\"." : "There is no such profile."),
            c => profiles is not null && profiles.Catalog.Find(c.Target) is not null);

        Add(CommandIds.NavBack, "Back", "Navigation", (c, ct) => Current()!.GoBackAsync(ct, Steps(c)), _ => Current()?.History.CanGoBack == true, "Back", "Alt+Left");
        Add(CommandIds.NavForward, "Forward", "Navigation", (c, ct) => Current()!.GoForwardAsync(ct, Steps(c)), _ => Current()?.History.CanGoForward == true, "Forward", "Alt+Right");
        Add(CommandIds.NavUp, "Up", "Navigation", (_, ct) => Current()!.GoUpAsync(ct), _ => Current()?.History.CanGoUp == true, "Up", "Alt+Up");
        // F5 means "look again": the cached rows are not shown first. A tab that was never listed (restored, or just opened) is not a refresh.
        Add(CommandIds.NavReload, "Reload", "Navigation", (_, ct) => Current()!.ReloadAsync(ct, forceFresh: Current()!.Folder.State is not Folders.FolderLoadState.Idle), _ => Current() is not null, "Refresh", "F5");

        Add(CommandIds.NavGo, "Go to address", "Navigation", async (c, ct) =>
        {
            if (Current() is not { } tab)
            {
                return;
            }

            var result = await resolver.ResolveAsync(c.Target, tab.History.Current, ct);
            switch (result.Outcome)
            {
                case AddressOutcome.Navigate or AddressOutcome.NavigateAndSelect:
                    await tab.NavigateAsync(result.Folder!, ct);
                    if (result.SelectItem is { } item && tab.Items.Rows.FirstOrDefault(r => r.Location == item) is { } entry)
                    {
                        tab.Selection.Click(entry);
                    }

                    break;
                default:
                    shell.ShowMessage(result.Message ?? "That location could not be opened.");
                    break;
            }
        }, _ => Current() is not null);

        Add(CommandIds.ItemOpen, "Open", "Files", async (c, ct) =>
        {
            if (c.Parameter is not FileEntry entry || Current() is not { } tab)
            {
                return;
            }

            if (Folders.FileColumns.IsFolderLike(entry))
            {
                await tab.NavigateAsync(entry.Location, ct);
                return;
            }

            var launched = await launcher.OpenAsync(entry.Location, ct);
            if (!launched.Started && !launched.Cancelled)
            {
                shell.ShowMessage(launched.Error ?? $"{entry.Name} could not be opened.");
            }
        }, c => c.Parameter is FileEntry, shortcut: "Enter");

        // Tiles and Gallery need media data and arrive in Phase 3 (build plan, D5); until then they are not offered.
        Add(CommandIds.ViewLayout, "Change layout", "View", (c, _) => Run(() =>
        {
            Current()!.SetLayout((Views.ViewLayout)c.Parameter!);
        }), c => Current() is not null && c.Parameter is Views.ViewLayout.Details or Views.ViewLayout.List or Views.ViewLayout.Thumbnails);

        Add(CommandIds.ViewDetails, "Details layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Details)), _ => Current() is not null, shortcut: "Ctrl+Shift+6");
        Add(CommandIds.ViewList, "List layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.List)), _ => Current() is not null, shortcut: "Ctrl+Shift+5");
        Add(CommandIds.ViewThumbnails, "Thumbnails layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Thumbnails)), _ => Current() is not null, shortcut: "Ctrl+Shift+2");

        Add(CommandIds.ViewZoom, "Resize thumbnails", "View", (c, _) => Run(() => Current()!.ZoomThumbnails((int)c.Parameter!)),
            c => Current() is { Layout: Views.ViewLayout.Thumbnails } && c.Parameter is int);

        Add(CommandIds.ViewThumbnailSize, "Set thumbnail size", "View", (c, _) => Run(() => Current()!.SetThumbnailSize((int)c.Parameter!)),
            c => Current() is { Layout: Views.ViewLayout.Thumbnails } && c.Parameter is int);

        // Selection (spec §6.2, nothing selected).
        Add(CommandIds.SelectAll, "Select all", "Selection", (_, _) => Run(() => Current()!.Selection.SelectAll()), _ => Current() is not null, shortcut: "Ctrl+A");
        Add(CommandIds.SelectNone, "Select none", "Selection", (_, _) => Run(() => Current()!.Selection.Clear()), _ => Current() is not null, shortcut: "Ctrl+Shift+A");
        Add(CommandIds.SelectInvert, "Invert selection", "Selection", (_, _) => Run(() => Current()!.Selection.Invert()), _ => Current() is not null, shortcut: "Ctrl+I");
        Add(CommandIds.SelectByPattern, "Select by pattern", "Selection", (c, _) => Run(() =>
        {
            var matched = Current()!.Selection.SelectByPattern(c.Target!);
            shell.ShowMessage(matched == 1 ? "1 item matches" : $"{matched} items match");
        }), c => Current() is not null && !string.IsNullOrWhiteSpace(c.Target));

        // New menu (spec §6.1). Creating is file I/O, so it runs on the thread pool inside NewItemService.
        async Task CreateNew(string templateId, CancellationToken ct)
        {
            var tab = Current()!;
            try
            {
                var created = await newItems!.CreateAsync(templateId, tab.History.Current, ct);
                shell.ShowMessage($"Created {created.Name}");
                await tab.ReloadAndSelectAsync(created, ct);

                // New items open with the rename box focused (spec §6.1).
                if (renamer is not null && tab.Items.Rows.FirstOrDefault(r => r.Location == created) is { } row)
                {
                    tab.BeginRename(row);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
            {
                shell.ShowMessage($"Could not create it: {ex.Message}");
            }
        }

        bool CanCreateHere() => newItems is not null && Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc;

        Add(CommandIds.NewFolder, "New folder", "New", (_, ct) => CreateNew(NewMenu.NewItemService.FolderTemplate.Id, ct), _ => CanCreateHere(), shortcut: "Ctrl+Shift+N", icon: "NewFolder");
        Add(CommandIds.NewItem, "New item", "New", (c, ct) => CreateNew(c.Target!, ct), c => CanCreateHere() && !string.IsNullOrEmpty(c.Target));

        // Inline rename (spec §8.4): F2 shows the box, the box's Enter / Tab / click-away runs RenameCommit.
        bool CanRenameHere() => renamer is not null && Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc && tab.RenameTarget() is not null;

        Add(CommandIds.EditRename, "Rename", "Edit", (_, _) => Run(() => Current()!.BeginRename(Current()!.RenameTarget()!)), _ => CanRenameHere(), shortcut: "F2", icon: "Rename");

        Add(CommandIds.RenameCommit, "Finish renaming", "Edit", async (c, ct) =>
        {
            var tab = Current()!;
            var location = tab.Renaming!;
            var text = c.Target?.Trim() ?? string.Empty;
            var goNext = c.Parameter is true;
            var renamed = location;
            if (text != location.Name)
            {
                if (Folders.RenameRules.Validate(text) is { } problem)
                {
                    // The box stays open so the name can be fixed.
                    shell.ShowMessage(problem);
                    return;
                }

                try
                {
                    renamed = await renamer!.RenameAsync(location, text, ct);
                    undo?.RecordRename(location.Value, renamed.Value);
                    shell.ShowMessage($"Renamed to {renamed.Name}");
                    await tab.ReloadAndSelectAsync(renamed, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    tab.CancelRename();
                    shell.ShowMessage($"Could not rename it: {ex.Message}");
                    return;
                }
            }

            tab.CancelRename();

            // Tab goes on to rename the next item (spec §8.4).
            if (goNext)
            {
                var rows = tab.Items.Rows;
                var at = -1;
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Location == renamed)
                    {
                        at = i;
                        break;
                    }
                }

                if (at >= 0 && at + 1 < rows.Count)
                {
                    tab.BeginRename(rows[at + 1]);
                }
            }
        }, c => renamer is not null && Current()?.Renaming is not null && c.Target is not null);

        Add(CommandIds.TerminalHere, "Open terminal here", "Files", async (_, ct) =>
        {
            var launched = await launcher.OpenTerminalAsync(Current()!.History.Current, TerminalKind.WindowsTerminal, ct);
            if (!launched.Started && !launched.Cancelled)
            {
                shell.ShowMessage(launched.Error ?? "The terminal could not be started.");
            }
        }, _ => Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive);

        // Commands on the selected item; the bar passes the entry as the parameter (spec §6.2).
        async Task Launch(Task<LaunchResult> task, FileEntry entry)
        {
            var launched = await task;
            if (!launched.Started && !launched.Cancelled)
            {
                shell.ShowMessage(launched.Error ?? $"{entry.Name} could not be started.");
            }
        }

        Add(CommandIds.RunAsAdministrator, "Run as administrator", "Files", (c, ct) => Launch(launcher.RunAsAdministratorAsync(((FileEntry)c.Parameter!).Location, ct), (FileEntry)c.Parameter!), c => c.Parameter is FileEntry { Kind: FileEntryKind.File });
        Add(CommandIds.OpenWith, "Open with…", "Files", (c, ct) => Launch(launcher.ChooseAppAsync(((FileEntry)c.Parameter!).Location, ct), (FileEntry)c.Parameter!), c => c.Parameter is FileEntry { Kind: FileEntryKind.File });

        // From a menu or button the item comes with the call; from the keyboard it is the one selected item.
        Add(CommandIds.Properties, "Properties", "Files", (c, ct) =>
        {
            var entry = c.Parameter as FileEntry ?? Current()!.RenameTarget()!;
            return Launch(launcher.ShowPropertiesAsync(entry.Location, ct), entry);
        }, c => c.Parameter is FileEntry || Current()?.RenameTarget() is not null, shortcut: "Alt+Enter");

        Add(CommandIds.FavoriteAdd, "Add to favorites", "Favorites", (c, _) => Run(() =>
        {
            var location = LocationPath.Parse(c.Target!);
            if (favorites!.Contains(location))
            {
                shell.ShowMessage($"{location.Name} is already in Favorites");
            }
            else
            {
                favorites.Toggle(location);
                shell.ShowMessage("Added to Favorites");
            }
        }), c => favorites is not null && LocationPath.TryParse(c.Target, out _));

        // Delete (spec §12.5): Del recycles, Shift+Del deletes for good after a confirmation.
        bool CanDeleteHere() => deleter is not null && Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc && tab.Selection.Count > 0;

        async Task DeleteSelection(bool permanent, CancellationToken ct)
        {
            var tab = Current()!;
            var items = tab.Selection.SelectedEntries.Select(e => e.Location).ToList();
            if (permanent)
            {
                var what = items.Count == 1 ? items[0].Name : $"these {items.Count} items";
                if (!await shell.ConfirmAsync("Delete permanently?", $"{what} will be deleted for good and cannot be restored.", "Delete"))
                {
                    return;
                }
            }

            shell.ShowMessage((await deleter!.DeleteAsync(items, permanent, ct)).Message);
        }

        Add(CommandIds.EditDelete, "Delete", "Edit", (_, ct) => DeleteSelection(false, ct), _ => CanDeleteHere(), shortcut: "Del", icon: "Delete");
        Add(CommandIds.EditDeletePermanent, "Delete permanently", "Edit", (_, ct) => DeleteSelection(true, ct), _ => CanDeleteHere(), shortcut: "Shift+Del");

        // Clipboard (spec §6.1): cut and copy put the selection on the Windows clipboard, paste hands it to the transfer service,
        // so a paste is the same journaled copy or move a drop is. Pasting a cut moves; pasting a copy copies, even in the same volume.
        bool InFolder() => Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc;
        bool CanTakeSelection() => clipboard is not null && InFolder() && Current()!.Selection.Count > 0;
        bool CanPasteHere() => clipboard is { HasFiles: true } && InFolder();

        Task PutOnClipboard(Transfers.ClipboardOperation operation)
        {
            var items = Current()!.Selection.SelectedEntries.Select(e => e.Location).ToList();
            clipboard!.Set(items, operation);
            var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
            shell.ShowMessage(operation == Transfers.ClipboardOperation.Cut ? $"Cut {what}. Paste to move." : $"Copied {what}.");
            return Task.CompletedTask;
        }

        async Task PasteAsync(bool intoNewFolder, CancellationToken ct)
        {
            var tab = Current()!;
            var here = tab.History.Current;
            var contents = await clipboard!.GetAsync(ct);
            if (contents is not { Items.Count: > 0 })
            {
                shell.ShowMessage("There are no files on the clipboard to paste.");
                return;
            }

            var cut = contents.Operation == Transfers.ClipboardOperation.Cut;
            var modifiers = cut ? Transfers.DropModifiers.Shift : Transfers.DropModifiers.Ctrl;

            // Judge before creating anything, so a refused paste does not leave an empty folder behind.
            var decision = Transfers.DropRules.Decide(contents.Items, intoNewFolder ? here.Combine("New folder") : here, modifiers);
            if (!decision.Allowed)
            {
                shell.ShowMessage(decision.Tooltip);
                return;
            }

            var destination = here;
            if (intoNewFolder)
            {
                try
                {
                    destination = await newItems!.CreateAsync(NewMenu.NewItemService.FolderTemplate.Id, here, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
                {
                    shell.ShowMessage($"Could not create the folder: {ex.Message}");
                    return;
                }
            }

            var outcome = await (transfers ?? new Transfers.UnavailableTransferService())
                .StartAsync(new Transfers.TransferRequest(contents.Items, destination, decision.Mode), ct);
            shell.ShowMessage(outcome.Message);
            if (outcome.Started && cut)
            {
                // Like Explorer: a cut can be pasted once; the items are on their way and the clipboard no longer offers them.
                clipboard.Clear();
            }
        }

        Add(CommandIds.EditCut, "Cut", "Edit", (_, _) => PutOnClipboard(Transfers.ClipboardOperation.Cut), _ => CanTakeSelection(), shortcut: "Ctrl+X", icon: "Cut");
        Add(CommandIds.EditCopy, "Copy", "Edit", (_, _) => PutOnClipboard(Transfers.ClipboardOperation.Copy), _ => CanTakeSelection(), shortcut: "Ctrl+C", icon: "Copy");
        Add(CommandIds.EditPaste, "Paste", "Edit", (_, ct) => PasteAsync(false, ct), _ => CanPasteHere(), shortcut: "Ctrl+V", icon: "Paste");
        Add(CommandIds.EditPasteIntoNewFolder, "Paste into new folder", "Edit", (_, ct) => PasteAsync(true, ct), _ => CanPasteHere() && newItems is not null);

        // Undo and redo (spec §12.5): one history for the whole app. The view shows the result: the restored item is selected.
        async Task ShowUndoOutcome(Undo.UndoOutcome outcome, CancellationToken ct)
        {
            shell.ShowMessage(outcome.Message);
            if (Current() is { } changed)
            {
                // Items came back or went away in other folders too: whatever the cache holds for them is out of date.
                foreach (var touched in outcome.Touched.Select(LocationPath.Parse))
                {
                    changed.Folder.Cache?.Invalidate(touched, includeDescendants: true);
                    if (touched.Parent is { } parent)
                    {
                        changed.Folder.Cache?.Invalidate(parent);
                    }
                }
            }

            if (Current() is { } tab)
            {
                var here = tab.History.Current;
                var inHere = outcome.Touched.Select(LocationPath.Parse).Where(p => p.Parent == here).ToList();
                if (inHere.Count > 0)
                {
                    await tab.ReloadAndSelectAsync(inHere[0], ct);
                }
            }
        }

        Add(CommandIds.EditUndo, "Undo", "Edit", async (_, ct) => await ShowUndoOutcome(await undo!.UndoAsync(ct), ct), _ => undo is { CanUndo: true }, shortcut: "Ctrl+Z", icon: "Undo");
        Add(CommandIds.EditRedo, "Redo", "Edit", async (_, ct) => await ShowUndoOutcome(await undo!.RedoAsync(ct), ct), _ => undo is { CanRedo: true }, shortcut: "Ctrl+Y", icon: "Redo");

        // Show or hide the summary card of the drive in Target (spec §8.7); remembered per drive.
        Add(CommandIds.DriveCardToggle, "Show or hide the drive summary", "View", (c, _) => Run(() =>
        {
            var root = LocationPath.Parse(c.Target!);
            driveCards!.SetHidden(root, !driveCards.IsHidden(root));
        }), c => driveCards is not null && LocationPath.TryParse(c.Target, out var root) && Drives.DriveSummary.IsDriveRoot(root));

        // Dropped items (spec §8.4): the rules decide, the transfer service does the work. A refused drop says why.
        Add(CommandIds.DropItems, "Drop items here", "Files", async (c, ct) =>
        {
            var drop = (ItemDrop)c.Parameter!;
            var destination = LocationPath.Parse(c.Target!);
            var decision = Transfers.DropRules.Decide(drop.Sources, destination, drop.Modifiers);
            if (!decision.Allowed)
            {
                shell.ShowMessage(decision.Tooltip);
                return;
            }

            var outcome = await (transfers ?? new Transfers.UnavailableTransferService())
                .StartAsync(new Transfers.TransferRequest(drop.Sources, destination, decision.Mode), ct);
            shell.ShowMessage(outcome.Message);
        }, c => c.Parameter is ItemDrop { Sources.Count: > 0 } && LocationPath.TryParse(c.Target, out _));

        // Sort, group, filter (spec §6.3).
        Add(CommandIds.SortBy, "Sort by", "View", (c, _) => Run(() => Current()!.SetSort(c.Target!)),
            c => Current() is not null && Folders.FileColumns.TryGet(c.Target ?? string.Empty, out _));
        Add(CommandIds.SortDirection, "Sort direction", "View", (c, _) => Run(() =>
        {
            var tab = Current()!;
            tab.SetSort(tab.Items.Sort[0].Column, c.Target!.Equals("desc", StringComparison.OrdinalIgnoreCase) ? Views.SortDirection.Descending : Views.SortDirection.Ascending);
        }), c => Current() is not null && c.Target is not null && (c.Target.Equals("asc", StringComparison.OrdinalIgnoreCase) || c.Target.Equals("desc", StringComparison.OrdinalIgnoreCase)));
        Add(CommandIds.GroupBy, "Group by", "View", (c, _) => Run(() => Current()!.SetGroupBy(string.IsNullOrEmpty(c.Target) ? null : c.Target)),
            c => Current() is not null && (string.IsNullOrEmpty(c.Target) || Folders.FileColumns.TryGet(c.Target, out var column) && column.Group is not null));
        Add(CommandIds.FolderPlacement, "Folders position", "View", (c, _) => Run(() => Current()!.SetFolderPlacement(Enum.Parse<Views.FolderPlacement>(c.Target!, ignoreCase: true))),
            c => Current() is not null && Enum.TryParse<Views.FolderPlacement>(c.Target, ignoreCase: true, out _));
        Add(CommandIds.FilterSet, "Filter this folder", "View", (c, _) => Run(() => Current()!.SetFilter(c.Target)), _ => Current() is not null);
        Add(CommandIds.FilterFocus, "Focus filter box", "View", (_, _) => Run(shell.FocusFilterBox), shortcut: "Ctrl+Shift+F");

        Add(CommandIds.ViewHidden, "Show hidden items", "View", async (_, ct) =>
        {
            viewOptions!.ShowHidden = !viewOptions.ShowHidden;
            await Current()!.ReloadAsync(ct);
        }, _ => viewOptions is not null && Current() is not null, shortcut: "Ctrl+H");

        Add(CommandIds.ViewColumns, "Choose columns", "View", (c, _) => Run(() =>
        {
            var choice = (Folders.ColumnChoice)c.Parameter!;
            Current()!.ApplyColumns(choice.Ids, choice.ForAllOfType);
        }), c => Current() is not null && c.Parameter is Folders.ColumnChoice);
        Add(CommandIds.SettingsOpen, "Settings", "View", (c, _) => Run(() => window.SettingsSheet.Toggle(c.Parameter as string)), shortcut: "Ctrl+,");
        Add(CommandIds.HubToggle, "Status hub", "View", (c, _) => Run(() => window.Hub.Toggle(c.Parameter as Hub.HubTab?)), shortcut: "Ctrl+Shift+O");
        Add(CommandIds.ViewSidebar, "Sidebar", "View", (_, _) => Run(() => window.SidebarCollapsed = !window.SidebarCollapsed), shortcut: "Ctrl+B");
        Add(CommandIds.PreviewPane, "Preview pane", "View", (_, _) => Run(() => window.ShowPreview = !window.ShowPreview), shortcut: "Alt+P");

        Add(CommandIds.FavoriteRename, "Rename favorite", "Favorites", (c, _) => Run(() => favorites!.Rename(LocationPath.Parse(c.Target!), c.Parameter as string)),
            c => favorites is not null && LocationPath.TryParse(c.Target, out var l) && favorites.Contains(l));
        Add(CommandIds.FavoriteStyle, "Change favorite icon and colour", "Favorites", (c, _) => Run(() =>
        {
            var change = (Sidebar.FavoriteStyleChange)c.Parameter!;
            favorites!.SetStyle(LocationPath.Parse(c.Target!), change.Icon, change.Color);
        }), c => favorites is not null && c.Parameter is Sidebar.FavoriteStyleChange && LocationPath.TryParse(c.Target, out var l) && favorites.Contains(l));
        Add(CommandIds.FavoriteToggle, "Pin or unpin folder", "Favorites", (_, _) => Run(() =>
        {
            var pinned = favorites!.Toggle(Current()!.History.Current);
            shell.ShowMessage(pinned ? "Added to Favorites" : "Removed from Favorites");
        }), _ => favorites is not null && Current() is not null, shortcut: "Ctrl+D");

        Add(CommandIds.AddressFocus, "Focus address bar", "Navigation", (_, _) => Run(shell.FocusAddressBar), shortcut: "Ctrl+L");
        Add(CommandIds.AppDiagnostics, "Diagnostics", "App", (_, _) => Run(shell.OpenDiagnostics));

        return registry;
    }

    /// <summary>The key bindings out of the box (spec §17.3). Users change them in the settings file.</summary>
    public static KeyMap DefaultKeyMap()
    {
        var map = new KeyMap();
        foreach (var (gesture, command) in new (string, CommandId)[]
        {
            ("Ctrl+T", CommandIds.TabNew),
            ("Ctrl+W", CommandIds.TabClose),
            ("Ctrl+Shift+T", CommandIds.TabReopen),
            ("Ctrl+Shift+K", CommandIds.TabDuplicate),
            ("Ctrl+Tab", CommandIds.TabNext),
            ("Ctrl+Shift+Tab", CommandIds.TabPrevious),
            ("Ctrl+N", CommandIds.WindowNew),
            ("Alt+Left", CommandIds.NavBack),
            ("Alt+Right", CommandIds.NavForward),
            ("Alt+Up", CommandIds.NavUp),
            ("F5", CommandIds.NavReload),
            ("Ctrl+R", CommandIds.NavReload),
            ("Ctrl+L", CommandIds.AddressFocus),
            ("Alt+D", CommandIds.AddressFocus),
            ("Ctrl+Shift+PageUp", CommandIds.TabMoveLeft),
            ("Ctrl+Shift+PageDown", CommandIds.TabMoveRight),
            ("Ctrl+D", CommandIds.FavoriteToggle),
            ("Ctrl+A", CommandIds.SelectAll),
            ("Ctrl+I", CommandIds.SelectInvert),
            ("Ctrl+Shift+A", CommandIds.SelectNone),
            ("Ctrl+H", CommandIds.ViewHidden),
            ("Alt+Enter", CommandIds.Properties),
            ("Ctrl+Shift+N", CommandIds.NewFolder),
            ("Ctrl+Shift+F", CommandIds.FilterFocus),
            ("Del", CommandIds.EditDelete),
            ("Shift+Del", CommandIds.EditDeletePermanent),
            ("Ctrl+X", CommandIds.EditCut),
            ("Ctrl+C", CommandIds.EditCopy),
            ("Ctrl+V", CommandIds.EditPaste),
            ("Ctrl+Z", CommandIds.EditUndo),
            ("Ctrl+Y", CommandIds.EditRedo),
            ("Ctrl+Shift+Z", CommandIds.EditRedo),
            ("Alt+P", CommandIds.PreviewPane),
            ("Ctrl+B", CommandIds.ViewSidebar),
            ("Ctrl+Shift+O", CommandIds.HubToggle),
            ("Ctrl+,", CommandIds.SettingsOpen),
            ("F2", CommandIds.EditRename),
            ("Ctrl+Shift+6", CommandIds.ViewDetails),
            ("Ctrl+Shift+5", CommandIds.ViewList),
            ("Ctrl+Shift+2", CommandIds.ViewThumbnails),
        })
        {
            map.TryBind(new KeyBinding(KeyGesture.Parse(gesture), command));
        }

        return map;
    }

    private const int MaxTabLabel = 60;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    /// <summary>Runs a command nobody awaits (an event handler): expected failures reach the status bar, anything else is a bug and surfaces.</summary>
    private static async Task ReportFailures(Task<CommandResult> task, IShellActions shell)
    {
        try
        {
            await task;
        }
        catch (LocationException ex)
        {
            shell.ShowMessage(ex.Message);
        }
    }

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
