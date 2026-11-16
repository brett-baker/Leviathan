// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007


namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Select all, none, invert and by pattern (spec §6.2).</summary>
internal sealed class SelectionCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var shell = Scope.Shell;

        // Selection (spec §6.2, nothing selected).
        AddOnCurrent(CommandIds.SelectAll, "Select all", "Selection", (tab, _) => tab.Selection.SelectAll(), shortcut: "Ctrl+A");
        AddOnCurrent(CommandIds.SelectNone, "Select none", "Selection", (tab, _) => tab.Selection.Clear(), shortcut: "Ctrl+Shift+A");
        AddOnCurrent(CommandIds.SelectInvert, "Invert selection", "Selection", (tab, _) => tab.Selection.Invert(), shortcut: "Ctrl+I");
        AddOnCurrent(CommandIds.SelectByPattern, "Select by pattern", "Selection", (tab, c) =>
        {
            var matched = tab.Selection.SelectByPattern(c.Target!);
            shell.ShowMessage(matched == 1 ? "1 item matches" : $"{matched} items match");
        }, (_, c) => !string.IsNullOrWhiteSpace(c.Target));
    }
}
