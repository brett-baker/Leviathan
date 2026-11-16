// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using System.Text.RegularExpressions;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Opening, closing, moving, pinning, locking, naming and dropping tabs (spec §4.1).</summary>
internal sealed partial class TabCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var strip = Scope.Strip;
        var registry = Scope.Registry;
        var window = Scope.Window;
        var windows = Scope.Windows;
        var shell = Scope.Shell;
        var home = Scope.Home;

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

        Add(CommandIds.TabNew, "New tab", "Tabs", async (c, ct) =>
        {
            var location = LocationPath.TryParse(c.Target, out var parsed) ? parsed : home();
            var tab = strip.NewTab(location);
            await tab.ReloadAsync(ct);
        }, shortcut: "Ctrl+T", icon: "Add");

        AddOnTab(CommandIds.TabNewRight, "New tab to the right", "Tabs", async (tab, _, ct) =>
            await strip.NewTab(home(), placement: NewTabPlacement.Right, relativeTo: tab).ReloadAsync(ct));

        AddOnTab(CommandIds.TabNewLeft, "New tab to the left", "Tabs", async (tab, _, ct) =>
            await strip.NewTab(home(), placement: NewTabPlacement.Left, relativeTo: tab).ReloadAsync(ct));

        AddOnTab(CommandIds.TabClose, "Close tab", "Tabs", (tab, _) => strip.Close(tab), (tab, _) => !tab.IsPinned, shortcut: "Ctrl+W");

        Add(CommandIds.TabReopen, "Reopen closed tab", "Tabs", async (_, ct) =>
        {
            if (strip.Reopen() is { } tab)
            {
                await tab.ReloadAsync(ct);
            }
        }, _ => strip.CanReopen, shortcut: "Ctrl+Shift+T");

        AddOnTab(CommandIds.TabDuplicate, "Duplicate tab", "Tabs", async (tab, _, ct) => await strip.Duplicate(tab).ReloadAsync(ct), shortcut: "Ctrl+Shift+K");

        AddAction(CommandIds.TabNext, "Next tab", "Tabs", () => strip.Cycle(forward: true), _ => strip.Tabs.Count > 1, shortcut: "Ctrl+Tab");
        AddAction(CommandIds.TabPrevious, "Previous tab", "Tabs", () => strip.Cycle(forward: false), _ => strip.Tabs.Count > 1, shortcut: "Ctrl+Shift+Tab");
        AddAction(CommandIds.TabSelect, "Go to tab", "Tabs", c => strip.SelectByNumber(c.Parameter is int n ? n : 0), c => c.Parameter is int);

        AddOnTab(CommandIds.TabTogglePin, "Pin tab", "Tabs", (tab, _) => strip.SetPinned(tab, !tab.IsPinned));
        AddOnTab(CommandIds.TabToggleLock, "Lock tab", "Tabs", (tab, _) => tab.IsLocked = !tab.IsLocked);

        // Only a locked tab can be named: an unlocked tab always shows its folder, and unlocking takes the name away again.
        AddOnTab(CommandIds.TabRename, "Rename tab", "Tabs", (tab, c) =>
        {
            var label = c.Target?.Trim();
            tab.CustomTitle = string.IsNullOrEmpty(label) ? null : label.Length > MaxTabLabel ? label[..MaxTabLabel] : label;
        }, (tab, _) => tab.IsLocked);

        AddOnTab(CommandIds.TabColor, "Colour tab", "Tabs", (tab, c) =>
        {
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
        });

        AddOnTab(CommandIds.TabCloseOthers, "Close other tabs", "Tabs", (tab, _) => strip.CloseOthers(tab), (tab, _) => strip.Tabs.Any(t => t != tab && !t.IsPinned));
        AddOnTab(CommandIds.TabCloseRight, "Close tabs to the right", "Tabs", (tab, _) => strip.CloseToRight(tab),
            (tab, _) => strip.Tabs.Skip(strip.Tabs.IndexOf(tab) + 1).Any(t => !t.IsPinned));

        AddOnTab(CommandIds.TabMoveLeft, "Move tab left", "Tabs", (tab, _) => strip.Nudge(tab, -1), shortcut: "Ctrl+Shift+PageUp");
        AddOnTab(CommandIds.TabMoveRight, "Move tab right", "Tabs", (tab, _) => strip.Nudge(tab, 1), shortcut: "Ctrl+Shift+PageDown");
        AddWith<TabMove>(CommandIds.TabMove, "Move tab", "Tabs", move => strip.Move(move.Tab, move.Index));

        AddWith<TabTearOff>(CommandIds.TabTearOff, "Move tab to a new window", "Tabs", drag => windows.TearOff(window, drag.Tab, drag.ScreenX, drag.ScreenY));

        AddWith<TabDrop>(CommandIds.TabDrop, "Drop tab here", "Tabs", drop =>
        {
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
        });
        Add(CommandIds.TabOpenInNewTab, "Open in new tab", "Tabs", async (c, ct) =>
        {
            if (!LocationPath.TryParse(c.Target, out var location))
            {
                return;
            }

            // A folder opened from the tab you are in sits next to it, like a link opened in a browser.
            var tab = strip.NewTab(location, select: c.Parameter is true, placement: NewTabPlacement.Right);
            await tab.ReloadAsync(ct);
        }, c => !string.IsNullOrWhiteSpace(c.Target));
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
}
