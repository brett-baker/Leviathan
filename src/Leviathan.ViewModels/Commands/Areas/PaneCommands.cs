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

        // Split panes (spec §15.1, §6.3). The same key again closes the split, which keeps the pane that has the focus.
        AddOnTab(CommandIds.PaneSingle, "One pane", "View", (tab, _, ct) => tab.SetPanesAsync(PaneLayout.Single, ct));
        AddOnTab(CommandIds.PaneSideBySide, "Split side by side", "View",
            (tab, _, ct) => tab.SetPanesAsync(tab.Panes == PaneLayout.SideBySide ? PaneLayout.Single : PaneLayout.SideBySide, ct), shortcut: @"Ctrl+\");
        AddOnTab(CommandIds.PaneTopBottom, "Split top and bottom", "View",
            (tab, _, ct) => tab.SetPanesAsync(tab.Panes == PaneLayout.TopBottom ? PaneLayout.Single : PaneLayout.TopBottom, ct), shortcut: @"Ctrl+Shift+\");
        // Two panes work together (spec §15.1): F5 and F6 send the selection to the other pane's folder through the normal queue.
        async Task SendToOtherPane(TabViewModel tab, Transfers.TransferMode mode, CancellationToken ct)
        {
            if (tab.Other is not { } other)
            {
                return;
            }

            // The same rules as a drop with Ctrl (copy) or Shift (move) held: refuses a folder into itself, an item already there, a server root.
            var sources = new List<LocationPath>(tab.Focused.Selection.SelectedEntries.Select(e => e.Location));
            await TransferAsync(sources, other.History.Current, ModifiersFor(mode), ct);
        }

        static bool CanSendToOtherPane(TabViewModel? tab) => tab is { IsSplit: true } && tab.Focused.Selection.Count > 0;

        AddOnTab(CommandIds.PaneCopyToOther, "Copy to the other pane", "Files", (tab, _, ct) => SendToOtherPane(tab, Transfers.TransferMode.Copy, ct),
            (tab, _) => CanSendToOtherPane(tab), shortcut: "F5");
        AddOnTab(CommandIds.PaneMoveToOther, "Move to the other pane", "Files", (tab, _, ct) => SendToOtherPane(tab, Transfers.TransferMode.Move, ct),
            (tab, _) => CanSendToOtherPane(tab), shortcut: "F6");
        AddOnTab(CommandIds.PaneSwap, "Swap the panes", "View", (tab, _, ct) => tab.SwapPanesAsync(ct), (tab, _) => tab.IsSplit, shortcut: "Ctrl+Shift+S");
        AddOnTab(CommandIds.PaneSameFolder, "Show this folder in the other pane", "View",
            (tab, _, ct) => tab.Other!.NavigateAsync(tab.Focused.History.Current, ct), (tab, _) => tab.IsSplit, shortcut: "Alt+=");

        AddOnTab(CommandIds.OpenInSplitPane, "Open in split pane", "Navigation", async (tab, c, ct) =>
        {
            // The folder opens in the pane that does not have the focus; a tab with one pane splits first.
            if (!LocationPath.TryParse(c.Target, out var folder))
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
        }, (_, c) => LocationPath.TryParse(c.Target, out var _));
    }
}
