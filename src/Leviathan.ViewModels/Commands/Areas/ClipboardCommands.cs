// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Cut, copy and paste through the Windows clipboard and the transfer service (spec §6.1).</summary>
internal sealed class ClipboardCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var shell = Scope.Shell;
        var newItems = Scope.NewItems;
        var transfers = Scope.TransferService;
        var clipboard = Scope.Clipboard;

        // Clipboard (spec §6.1): cut and copy put the selection on the Windows clipboard, paste hands it to the transfer service,
        // so a paste is the same journaled copy or move a drop is. Pasting a cut moves; pasting a copy copies, even in the same volume.
        static bool InFolder(TabViewModel tab) => LocationKinds.IsFileOperable(tab.History.Current.Kind);
        bool CanTakeSelection(TabViewModel tab) => clipboard is not null && InFolder(tab) && tab.Selection.Count > 0;
        bool CanPasteHere(TabViewModel tab) => clipboard is { HasFiles: true } && InFolder(tab);

        void PutOnClipboard(TabViewModel tab, Transfers.ClipboardOperation operation)
        {
            var items = tab.Selection.SelectedEntries.Select(e => e.Location).ToList();
            clipboard!.Set(items, operation);
            var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
            shell.ShowMessage(operation == Transfers.ClipboardOperation.Cut ? $"Cut {what}. Paste to move." : $"Copied {what}.");
        }

        async Task PasteAsync(TabViewModel tab, bool intoNewFolder, CancellationToken ct)
        {
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

        // "Paste special": text or a picture on the clipboard becomes a file, named "Pasted text.txt" or "Pasted image.png" (numbered if
        // taken), then opens its rename box like any new item. Local folders and shares only: the file is written through the new-item creator.
        bool CanPasteContent(TabViewModel tab, bool present) => present && newItems is not null && LocationKinds.IsLocalOrShare(tab.History.Current.Kind);

        async Task PasteContentAsync(TabViewModel tab, string name, Func<Task<byte[]?>> read, string nothing, CancellationToken ct)
        {
            var bytes = await read();
            if (bytes is not { Length: > 0 })
            {
                shell.ShowMessage(nothing);
                return;
            }

            try
            {
                var created = await newItems!.CreateFileAsync(name, bytes, tab.History.Current, ct);
                shell.ShowMessage($"Created {created.Name}");
                await tab.ReloadAndSelectAsync(created, ct);
                if (Scope.Renamer is not null && tab.Items.Rows.FirstOrDefault(r => r.Location == created) is { } row)
                {
                    tab.BeginRename(row);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                shell.ShowMessage($"Could not paste it: {ex.Message}");
            }
        }

        AddOnCurrent(
            CommandIds.EditPasteTextAsFile,
            "Paste text as new file",
            "Edit",
            (tab, _, ct) => PasteContentAsync(
                tab,
                "Pasted text.txt",
                async () => await clipboard!.GetTextAsync(ct) is { } text ? System.Text.Encoding.UTF8.GetBytes(text) : null,
                "There is no text on the clipboard to paste.",
                ct),
            (tab, _) => CanPasteContent(tab, clipboard is { HasText: true }));
        AddOnCurrent(
            CommandIds.EditPasteImageAsFile,
            "Paste image as new file",
            "Edit",
            (tab, _, ct) => PasteContentAsync(tab, "Pasted image.png", () => clipboard!.GetImagePngAsync(ct), "There is no picture on the clipboard to paste.", ct),
            (tab, _) => CanPasteContent(tab, clipboard is { HasImage: true }));

        AddOnCurrent(CommandIds.EditCut, "Cut", "Edit", (tab, _) => PutOnClipboard(tab, Transfers.ClipboardOperation.Cut), (tab, _) => CanTakeSelection(tab), shortcut: "Ctrl+X", icon: "Cut");
        AddOnCurrent(CommandIds.EditCopy, "Copy", "Edit", (tab, _) => PutOnClipboard(tab, Transfers.ClipboardOperation.Copy), (tab, _) => CanTakeSelection(tab), shortcut: "Ctrl+C", icon: "Copy");
        AddOnCurrent(CommandIds.EditPaste, "Paste", "Edit", (tab, _, ct) => PasteAsync(tab, false, ct), (tab, _) => CanPasteHere(tab), shortcut: "Ctrl+V", icon: "Paste");
        AddOnCurrent(CommandIds.EditPasteIntoNewFolder, "Paste into new folder", "Edit", (tab, _, ct) => PasteAsync(tab, true, ct), (tab, _) => CanPasteHere(tab) && newItems is not null);
    }
}
