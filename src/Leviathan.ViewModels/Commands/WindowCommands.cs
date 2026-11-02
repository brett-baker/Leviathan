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

    /// <summary>Shows a short message in the status bar, e.g. why an address could not be opened.</summary>
    void ShowMessage(string message);
}

/// <summary>Stable ids of the window-level commands (spec §6, §17.3).</summary>
public static class CommandIds
{
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
        Func<LocationPath> home)
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

        Add(CommandIds.NavBack, "Back", "Navigation", (c, ct) => Current()!.GoBackAsync(ct, Steps(c)), _ => Current()?.History.CanGoBack == true, "Back", "Alt+Left");
        Add(CommandIds.NavForward, "Forward", "Navigation", (c, ct) => Current()!.GoForwardAsync(ct, Steps(c)), _ => Current()?.History.CanGoForward == true, "Forward", "Alt+Right");
        Add(CommandIds.NavUp, "Up", "Navigation", (_, ct) => Current()!.GoUpAsync(ct), _ => Current()?.History.CanGoUp == true, "Up", "Alt+Up");
        Add(CommandIds.NavReload, "Reload", "Navigation", (_, ct) => Current()!.ReloadAsync(ct), _ => Current() is not null, "Refresh", "F5");

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
