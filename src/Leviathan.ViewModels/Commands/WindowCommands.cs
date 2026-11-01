// View models are UI-thread affine: everything a control can observe is changed on the thread that started the command, so the
// awaits in this file deliberately resume on the caller's context (crash signature 52a0ec04c34d: RPC_E_WRONG_THREAD from a
// continuation that ran on the thread pool). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
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

    /// <summary>Focus the address bar (Ctrl+L).</summary>
    public static readonly CommandId AddressFocus = new("address.focus");

    /// <summary>Open the diagnostics window.</summary>
    public static readonly CommandId AppDiagnostics = new("app.diagnostics");
}

/// <summary>
/// Registers the commands of one window against its tabs (build plan, Phase 1: every user action is a command). The UI
/// buttons, menus, shortcuts and, later, the command palette all call these, so the action trail sees everything the user does.
/// </summary>
public static class WindowCommands
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

        TabViewModel? Current() => strip.Selected;

        void Add(CommandId id, string title, string category, Func<CommandContext, CancellationToken, Task> run, Func<CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
            registry.Register(new AppCommand(new CommandDefinition(id, title, category, icon, shortcut), run, can));

        static int Steps(CommandContext context) => context.Parameter is int steps and > 0 ? steps : 1;

        Add(CommandIds.TabNew, "New tab", "Tabs", async (c, ct) =>
        {
            var location = LocationPath.TryParse(c.Target, out var parsed) ? parsed : home();
            var tab = strip.NewTab(location);
            await tab.ReloadAsync(ct);
        }, shortcut: "Ctrl+T", icon: "Add");

        Add(CommandIds.TabClose, "Close tab", "Tabs", (_, _) =>
        {
            if (Current() is { } tab)
            {
                strip.Close(tab);
            }

            return Task.CompletedTask;
        }, _ => Current() is { IsPinned: false }, shortcut: "Ctrl+W");

        Add(CommandIds.TabReopen, "Reopen closed tab", "Tabs", async (_, ct) =>
        {
            if (strip.Reopen() is { } tab)
            {
                await tab.ReloadAsync(ct);
            }
        }, _ => strip.CanReopen, shortcut: "Ctrl+Shift+T");

        Add(CommandIds.TabDuplicate, "Duplicate tab", "Tabs", async (_, ct) =>
        {
            if (Current() is { } tab)
            {
                await strip.Duplicate(tab).ReloadAsync(ct);
            }
        }, _ => Current() is not null, shortcut: "Ctrl+Shift+K");

        Add(CommandIds.TabNext, "Next tab", "Tabs", (_, _) => Run(() => strip.Cycle(forward: true)), _ => strip.Tabs.Count > 1, shortcut: "Ctrl+Tab");
        Add(CommandIds.TabPrevious, "Previous tab", "Tabs", (_, _) => Run(() => strip.Cycle(forward: false)), _ => strip.Tabs.Count > 1, shortcut: "Ctrl+Shift+Tab");
        Add(CommandIds.TabSelect, "Go to tab", "Tabs", (c, _) => Run(() => strip.SelectByNumber(c.Parameter is int n ? n : 0)), c => c.Parameter is int);

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
        })
        {
            map.TryBind(new KeyBinding(KeyGesture.Parse(gesture), command));
        }

        return map;
    }

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
