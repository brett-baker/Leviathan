// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.ViewModels.Navigation;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Back, forward, up, reload, go to an address, the command palette and the address bar.</summary>
internal sealed class NavigationCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var resolver = Scope.Resolver;
        var shell = Scope.Shell;

        AddOnCurrent(CommandIds.NavBack, "Back", "Navigation", (tab, c, ct) => tab.GoBackAsync(ct, Steps(c)), (tab, _) => tab.History.CanGoBack, "Back", "Alt+Left");
        AddOnCurrent(CommandIds.NavForward, "Forward", "Navigation", (tab, c, ct) => tab.GoForwardAsync(ct, Steps(c)), (tab, _) => tab.History.CanGoForward, "Forward", "Alt+Right");
        AddOnCurrent(CommandIds.NavUp, "Up", "Navigation", (tab, _, ct) => tab.GoUpAsync(ct), (tab, _) => tab.History.CanGoUp, "Up", "Alt+Up");
        // F5 means "look again": the cached rows are not shown first. A tab that was never listed (restored, or just opened) is not a refresh.
        AddOnCurrent(CommandIds.NavReload, "Reload", "Navigation", (tab, _, ct) => tab.ReloadAsync(ct, forceFresh: tab.Folder.State is not Folders.FolderLoadState.Idle), icon: "Refresh", shortcut: "F5");

        AddOnCurrent(CommandIds.NavGo, "Go to address", "Navigation", async (tab, c, ct) =>
        {
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
        });

        AddAction(CommandIds.PaletteCommands, "Command palette", "Navigation", () => shell.OpenPalette(Palette.PaletteMode.Commands), shortcut: "Ctrl+Shift+P");
        AddAction(CommandIds.PaletteGoTo, "Go to...", "Navigation", () => shell.OpenPalette(Palette.PaletteMode.GoTo), shortcut: "Ctrl+P");

        AddAction(CommandIds.AddressFocus, "Focus address bar", "Navigation", shell.FocusAddressBar, shortcut: "Ctrl+L");
    }
}
