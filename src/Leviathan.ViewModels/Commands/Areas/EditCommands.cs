// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Rename, create, delete and undo (spec §6.1, §8.4, §12.5, §15.3).</summary>
internal sealed class EditCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var shell = Scope.Shell;
        var newItems = Scope.NewItems;
        var renamer = Scope.Renamer;
        var deleter = Scope.Deleter;
        var undo = Scope.UndoService;

        // New menu (spec §6.1). Creating is file I/O, so it runs on the thread pool inside NewItemService.
        async Task CreateNew(TabViewModel tab, string templateId, CancellationToken ct)
        {
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

        bool CanCreateIn(TabViewModel tab) => newItems is not null && (tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc || newItems.Remote?.Handles(tab.History.Current) == true);

        AddOnCurrent(CommandIds.NewFolder, "New folder", "New", (tab, _, ct) => CreateNew(tab, NewMenu.NewItemService.FolderTemplate.Id, ct), (tab, _) => CanCreateIn(tab), shortcut: "Ctrl+Shift+N", icon: "NewFolder");
        AddOnCurrent(CommandIds.NewItem, "New item", "New", (tab, c, ct) => CreateNew(tab, c.Target!, ct), (tab, c) => CanCreateIn(tab) && !string.IsNullOrEmpty(c.Target));

        // Inline rename (spec §8.4): F2 shows the box, the box's Enter / Tab / click-away runs RenameCommit.
        bool CanBulkRename(TabViewModel tab) => tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc && tab.Selection.SelectedEntries.Count > 0;

        bool CanRename(TabViewModel tab) => renamer is not null && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc or LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps && tab.RenameTarget() is not null;

        // F2 renames the one selected item in place; with several selected it opens the bulk-rename dialog (spec §15.3).
        AddOnCurrent(CommandIds.EditRename, "Rename", "Edit", (tab, _) =>
        {
            if (tab.RenameTarget() is { } one)
            {
                tab.BeginRename(one);
            }
            else
            {
                shell.BulkRename();
            }
        }, (tab, _) => CanRename(tab) || (CanBulkRename(tab) && tab.Selection.SelectedEntries.Count > 1), shortcut: "F2", icon: "Rename");
        AddOnCurrent(CommandIds.BulkRename, "Bulk rename…", "Edit", (_, _) => shell.BulkRename(), (tab, _) => CanBulkRename(tab), shortcut: "Shift+F2");

        AddOnCurrent(CommandIds.RenameCommit, "Finish renaming", "Edit", async (tab, c, ct) =>
        {
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
        }, (tab, c) => renamer is not null && tab.Renaming is not null && c.Target is not null);

        // Delete (spec §12.5): Del recycles, Shift+Del deletes for good after a confirmation.
        bool CanDeleteIn(TabViewModel tab) => deleter is not null && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc or LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps && tab.Selection.Count > 0;

        async Task DeleteSelection(TabViewModel tab, bool permanent, CancellationToken ct)
        {
            var items = tab.Selection.SelectedEntries.Select(e => e.Location).ToList();
            var what = items.Count == 1 ? items[0].Name : $"these {items.Count} items";

            // Plain Delete on a drive with no Recycle Bin (a USB stick) is permanent, so it is confirmed like Shift+Del (spec §12.5).
            var noBin = !permanent && await deleter!.BehaviorAsync(items, ct) == Core.Platform.DeleteBehavior.PermanentAfterConfirmation;
            if (permanent || noBin)
            {
                var why = noBin ? (items[0].Kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps ? "A server has no Recycle Bin. " : "This drive has no Recycle Bin. ") : string.Empty;
                if (!await shell.ConfirmAsync("Delete permanently?", $"{why}{what} will be deleted for good and cannot be restored.", "Delete"))
                {
                    return;
                }

                permanent = true;
            }

            shell.ShowMessage((await deleter!.DeleteAsync(items, permanent, ct)).Message);
        }

        AddOnCurrent(CommandIds.EditDelete, "Delete", "Edit", (tab, _, ct) => DeleteSelection(tab, false, ct), (tab, _) => CanDeleteIn(tab), shortcut: "Del", icon: "Delete");
        AddOnCurrent(CommandIds.EditDeletePermanent, "Delete permanently", "Edit", (tab, _, ct) => DeleteSelection(tab, true, ct), (tab, _) => CanDeleteIn(tab), shortcut: "Shift+Del");

        // Undo and redo (spec §12.5): one history for the whole app. The view shows the result: the restored item is selected.
        async Task ShowUndoOutcome(Undo.UndoOutcome outcome, CancellationToken ct)
        {
            shell.ShowMessage(outcome.Message);
            if (Current() is { } changed)
            {
                // Items came back or went away in other folders too: whatever the cache holds for them is out of date.
                foreach (var touched in outcome.Touched.Select(LocationPath.ParseExact))
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
                var inHere = outcome.Touched.Select(LocationPath.ParseExact).Where(p => p.Parent == here).ToList();
                if (inHere.Count > 0)
                {
                    await tab.ReloadAndSelectAsync(inHere[0], ct);
                }
            }
        }

        Add(CommandIds.EditUndo, "Undo", "Edit", async (_, ct) => await ShowUndoOutcome(await undo!.UndoAsync(ct), ct), _ => undo is { CanUndo: true }, shortcut: "Ctrl+Z", icon: "Undo");
        Add(CommandIds.EditRedo, "Redo", "Edit", async (_, ct) => await ShowUndoOutcome(await undo!.RedoAsync(ct), ct), _ => undo is { CanRedo: true }, shortcut: "Ctrl+Y", icon: "Redo");
    }
}
