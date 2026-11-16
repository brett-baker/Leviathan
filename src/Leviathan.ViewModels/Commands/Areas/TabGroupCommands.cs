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

        // A command on one of this strip's groups; the group comes as the parameter, and is checked to still be here.
        void AddOnGroup(CommandId id, string title, Action<TabGroup, CommandContext> run, Func<TabGroup, CommandContext, bool>? can = null) =>
            AddAction(id, title, "Tabs", c => run(GroupOfParameter(c)!, c), c => GroupOfParameter(c) is { } group && (can?.Invoke(group, c) ?? true));

        AddOnTab(CommandIds.TabGroupNew, "Add tab to new group", "Tabs", (tab, c) => strip.CreateGroup([tab], c.Target), (tab, _) => !tab.IsPinned);
        AddWith<TabGroupAdd>(CommandIds.TabGroupAdd, "Add tab to group", "Tabs", add => strip.AddToGroup(add.Tab, add.Group, add.Position),
            add => !add.Tab.IsPinned && strip.Groups.Contains(add.Group) && strip.Tabs.Contains(add.Tab));
        AddOnTab(CommandIds.TabGroupRemove, "Remove tab from group", "Tabs", (tab, _) => strip.RemoveFromGroup(tab), (tab, _) => tab.GroupId is not null);
        AddOnGroup(CommandIds.TabGroupRename, "Rename group", (group, c) => group.Name = (c.Target ?? string.Empty).Trim());
        AddOnGroup(CommandIds.TabGroupColor, "Change group colour", (group, c) => group.Color = c.Target!.ToUpperInvariant(), (_, c) => TabGroupColors.IsColor(c.Target));
        AddOnGroup(CommandIds.TabGroupCollapse, "Collapse or expand group", (group, c) =>
        {
            var collapse = c.Target is null ? !group.IsCollapsed : !c.Target.Equals("false", StringComparison.OrdinalIgnoreCase);
            if (!strip.SetCollapsed(group, collapse))
            {
                shell.ShowMessage("The group holds every tab in this window, so it cannot be folded away.");
            }
        });
        AddOnGroup(CommandIds.TabGroupUngroup, "Ungroup", (group, _) => strip.Ungroup(group));
        AddOnGroup(CommandIds.TabGroupClose, "Close group", (group, _) => strip.CloseGroup(group));
        Add(CommandIds.TabGroupNewTab, "New tab in group", "Tabs", async (c, ct) =>
        {
            var tab = strip.NewTabInGroup(GroupOfParameter(c)!, LocationPath.TryParse(c.Target, out var parsed) ? parsed : null);
            if (tab is not null)
            {
                await tab.ReloadAsync(ct);
            }
        }, c => GroupOfParameter(c) is not null);
        AddOnGroup(CommandIds.TabGroupToWindow, "Move group to new window", (group, _) =>
        {
            var bounds = window.Bounds.Rect;
            if (windows.TearOffGroup(window, group, bounds.X + 48, bounds.Y + 48) is null)
            {
                shell.ShowMessage("The group holds every tab in this window, so there is nothing to move.");
            }
        });
        AddWith<TabGroupMove>(CommandIds.TabGroupMove, "Move group", "Tabs", move => strip.MoveGroup(move.Group, move.Index), move => strip.Groups.Contains(move.Group));
        AddWith<TabGroupTearOff>(CommandIds.TabGroupTearOff, "Move group to a new window", "Tabs", drag => windows.TearOffGroup(window, drag.Group, drag.ScreenX, drag.ScreenY));
        AddWith<TabGroupDrop>(CommandIds.TabGroupDrop, "Drop group here", "Tabs", drop =>
        {
            var from = windows.Windows.FirstOrDefault(w => w.Tabs.Groups.Contains(drop.Group));
            if (from == window)
            {
                strip.MoveGroup(drop.Group, drop.Index ?? strip.Tabs.Count);
            }
            else if (from is not null)
            {
                windows.MergeGroup(from, drop.Group, window, drop.Index);
            }
        });    }
}
