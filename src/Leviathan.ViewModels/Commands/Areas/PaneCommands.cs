// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Split panes and the commands that work across the two panes (spec §15.1, §6.3).</summary>
internal sealed class PaneCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var shell = Scope.Shell;
        var transfers = Scope.TransferService;

        // Split panes (spec §15.1, §6.3). The same key again closes the split, which keeps the pane that has the focus.
        Add(CommandIds.PaneSingle, "One pane", "View", (c, ct) => TabOf(c)!.SetPanesAsync(PaneLayout.Single, ct), c => TabOf(c) is not null);
        Add(CommandIds.PaneSideBySide, "Split side by side", "View",
            (c, ct) => TabOf(c)!.SetPanesAsync(TabOf(c)!.Panes == PaneLayout.SideBySide ? PaneLayout.Single : PaneLayout.SideBySide, ct),
            c => TabOf(c) is not null, shortcut: @"Ctrl+\");
        Add(CommandIds.PaneTopBottom, "Split top and bottom", "View",
            (c, ct) => TabOf(c)!.SetPanesAsync(TabOf(c)!.Panes == PaneLayout.TopBottom ? PaneLayout.Single : PaneLayout.TopBottom, ct),
            c => TabOf(c) is not null, shortcut: @"Ctrl+Shift+\");
        // Two panes work together (spec §15.1): F5 and F6 send the selection to the other pane's folder through the normal queue.
        async Task SendToOtherPane(CommandContext c, Transfers.TransferMode mode, CancellationToken ct)
        {
            if (TabOf(c) is not { Other: { } other } tab)
            {
                return;
            }

            var sources = new List<LocationPath>(tab.Focused.Selection.SelectedEntries.Select(e => e.Location));
            var destination = other.History.Current;

            // The same rules as a drop with Ctrl (copy) or Shift (move) held: refuses a folder into itself, an item already there, a server root.
            var decision = Transfers.DropRules.Decide(sources, destination, mode == Transfers.TransferMode.Copy ? Transfers.DropModifiers.Ctrl : Transfers.DropModifiers.Shift);
            if (!decision.Allowed)
            {
                shell.ShowMessage(decision.Tooltip);
                return;
            }

            var outcome = await (transfers ?? new Transfers.UnavailableTransferService())
                .StartAsync(new Transfers.TransferRequest(sources, destination, decision.Mode), ct);
            shell.ShowMessage(outcome.Message);
        }

        static bool CanSendToOtherPane(TabViewModel? tab) => tab is { IsSplit: true } && tab.Focused.Selection.Count > 0;

        Add(CommandIds.PaneCopyToOther, "Copy to the other pane", "Files", (c, ct) => SendToOtherPane(c, Transfers.TransferMode.Copy, ct),
            c => CanSendToOtherPane(TabOf(c)), shortcut: "F5");
        Add(CommandIds.PaneMoveToOther, "Move to the other pane", "Files", (c, ct) => SendToOtherPane(c, Transfers.TransferMode.Move, ct),
            c => CanSendToOtherPane(TabOf(c)), shortcut: "F6");
        Add(CommandIds.PaneSwap, "Swap the panes", "View", (c, ct) => TabOf(c)!.SwapPanesAsync(ct), c => TabOf(c) is { IsSplit: true }, shortcut: "Ctrl+Shift+S");
        Add(CommandIds.PaneSameFolder, "Show this folder in the other pane", "View",
            (c, ct) => TabOf(c)!.Other!.NavigateAsync(TabOf(c)!.Focused.History.Current, ct), c => TabOf(c) is { IsSplit: true }, shortcut: "Alt+=");

        Add(CommandIds.OpenInSplitPane, "Open in split pane", "Navigation", async (c, ct) =>
        {
            // The folder opens in the pane that does not have the focus; a tab with one pane splits first.
            if (TabOf(c) is not { } tab || !LocationPath.TryParse(c.Target, out var folder))
            {
                return;
            }

            if (!tab.IsSplit)
            {
                await tab.SetPanesAsync(PaneLayout.SideBySide, ct);
            }

            if (tab.Other is { } other)
            {
                await other.NavigateAsync(folder, ct);
            }
        }, c => TabOf(c) is not null && LocationPath.TryParse(c.Target, out _));
    }
}
