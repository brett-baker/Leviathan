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

        Add(CommandIds.TabNewRight, "New tab to the right", "Tabs", async (c, ct) =>
        {
            var tab = strip.NewTab(home(), placement: NewTabPlacement.Right, relativeTo: TabOf(c));
            await tab.ReloadAsync(ct);
        }, c => TabOf(c) is not null);

        Add(CommandIds.TabNewLeft, "New tab to the left", "Tabs", async (c, ct) =>
        {
            var tab = strip.NewTab(home(), placement: NewTabPlacement.Left, relativeTo: TabOf(c));
            await tab.ReloadAsync(ct);
        }, c => TabOf(c) is not null);

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

        // Only a locked tab can be named: an unlocked tab always shows its folder, and unlocking takes the name away again.
        Add(CommandIds.TabRename, "Rename tab", "Tabs", (c, _) => Run(() =>
        {
            if (TabOf(c) is { IsLocked: true } tab)
            {
                var label = c.Target?.Trim();
                tab.CustomTitle = string.IsNullOrEmpty(label) ? null : label.Length > MaxTabLabel ? label[..MaxTabLabel] : label;
            }
        }), c => TabOf(c) is { IsLocked: true });

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
