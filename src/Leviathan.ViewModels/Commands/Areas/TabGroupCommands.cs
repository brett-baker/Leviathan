// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Tab groups: create, fill, rename, colour, fold, move and tear off (spec §4.1, D32). Parameters are the strip's own objects, so a group menu or a drag hands them straight over.</summary>
internal sealed class TabGroupCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var strip = Scope.Strip;
        var window = Scope.Window;
        var windows = Scope.Windows;
        var shell = Scope.Shell;

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
    }
}
