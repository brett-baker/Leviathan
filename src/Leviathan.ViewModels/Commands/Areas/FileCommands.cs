// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Opening items and handing them to other programs, the terminal, drops, and the quick move and copy targets (spec §8.4, §15.6, §15.7).</summary>
internal sealed class FileCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var launcher = Scope.Launcher;
        var shell = Scope.Shell;
        var transfers = Scope.TransferService;
        var terminal = Scope.Terminal;
        var quickTargets = Scope.QuickTargetStore;

        Add(CommandIds.ItemOpen, "Open", "Files", async (c, ct) =>
        {
            if (c.Parameter is not FileEntry entry || Current() is not { } tab)
            {
                return;
            }

            // An archive opens as a folder (spec §15.4); "Open with" is how to hand it to another program. An archive inside an
            // archive is only a file, and is unpacked and opened by its program.
            if (Folders.FileColumns.IsFolderLike(entry) || (entry.Kind == FileEntryKind.File && Core.Archives.ArchiveKinds.IsBrowsable(entry.Extension) && !Core.Archives.ArchiveKinds.IsInside(entry.Location)))
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

        // Quick move and copy targets (spec §15.6): nine slots, the same queued transfer as a drop, in every folder.
        async Task SendToQuickTarget(CommandContext c, int slot, Transfers.TransferMode mode, CancellationToken ct)
        {
            if (Current() is not { } tab)
            {
                return;
            }

            if (QuickTargets.FolderOf(quickTargets?.Slots ?? [], slot) is not { } destination)
            {
                shell.ShowMessage($"Quick target {slot} is not set. Open the folder you want and run \"Make this folder quick target {slot}\".");
                return;
            }

            var sources = new List<LocationPath>((c.Parameter is FileEntry one ? [one] : tab.Selection.SelectedEntries).Select(e => e.Location));
            var decision = Transfers.DropRules.Decide(sources, destination, mode == Transfers.TransferMode.Copy ? Transfers.DropModifiers.Ctrl : Transfers.DropModifiers.Shift);
            if (!decision.Allowed)
            {
                shell.ShowMessage(decision.Tooltip);
                return;
            }

            var outcome = await (transfers ?? new Transfers.UnavailableTransferService()).StartAsync(new Transfers.TransferRequest(sources, destination, decision.Mode), ct);
            shell.ShowMessage(outcome.Message);
        }

        bool HasSelection(CommandContext c) => c.Parameter is FileEntry || Current() is { Selection.Count: > 0 };
        for (var n = 1; n <= QuickTargets.Count; n++)
        {
            var slot = n;
            Add(QuickTargets.MoveId(slot), $"Move to quick target {slot}", "Files", (c, ct) => SendToQuickTarget(c, slot, Transfers.TransferMode.Move, ct), HasSelection, shortcut: $"Ctrl+Alt+{slot}");
            Add(QuickTargets.CopyId(slot), $"Copy to quick target {slot}", "Files", (c, ct) => SendToQuickTarget(c, slot, Transfers.TransferMode.Copy, ct), HasSelection, shortcut: $"Ctrl+Alt+Shift+{slot}");
            Add(QuickTargets.SetId(slot), $"Make this folder quick target {slot}", "Files", async (_, ct) =>
            {
                if (quickTargets is null || Current() is not { } tab)
                {
                    return;
                }

                var folder = tab.History.Current;
                if (!TerminalTargets.IsUsable(folder) && folder.Kind is not (LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps))
                {
                    shell.ShowMessage("This location cannot be a quick target.");
                    return;
                }

                await quickTargets.SetAsync(slot, folder.Value, ct);
                shell.ShowMessage($"Quick target {slot} is now {folder.Value}.");
            }, _ => quickTargets is not null && Current() is not null);
        }

        // Open terminal here (spec §15.7): the selected folder, else the selected file's folder, else the folder shown; the terminal is the one
        // chosen in the settings, and Windows Terminal falls back to PowerShell when it is not installed.
        LocationPath? TerminalFolder(CommandContext c) =>
            Current() is { } tab ? TerminalTargets.FolderFor(c.Parameter is FileEntry one ? [one] : [.. tab.Selection.SelectedEntries], tab.History.Current) : null;
        Add(CommandIds.TerminalHere, "Open terminal here", "Files", async (c, ct) =>
        {
            if (TerminalFolder(c) is not { } folder)
            {
                return;
            }

            var wanted = terminal?.Invoke() ?? TerminalKind.WindowsTerminal;
            var launched = await launcher.OpenTerminalAsync(folder, wanted, ct);
            if (!launched.Started && !launched.Cancelled && TerminalTargets.FallbackFor(wanted) is { } fallback)
            {
                launched = await launcher.OpenTerminalAsync(folder, fallback, ct);
                if (launched.Started)
                {
                    shell.ShowMessage($"{TerminalTargets.Title(wanted)} could not be started, so {TerminalTargets.Title(fallback)} was opened instead.");
                    return;
                }
            }

            if (!launched.Started && !launched.Cancelled)
            {
                shell.ShowMessage(launched.Error ?? "The terminal could not be started.");
            }
        }, c => TerminalFolder(c) is not null, shortcut: "Ctrl+Alt+T");

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
    }
}
