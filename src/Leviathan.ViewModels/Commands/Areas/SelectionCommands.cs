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
        Add(CommandIds.SelectAll, "Select all", "Selection", (_, _) => Run(() => Current()!.Selection.SelectAll()), _ => Current() is not null, shortcut: "Ctrl+A");
        Add(CommandIds.SelectNone, "Select none", "Selection", (_, _) => Run(() => Current()!.Selection.Clear()), _ => Current() is not null, shortcut: "Ctrl+Shift+A");
        Add(CommandIds.SelectInvert, "Invert selection", "Selection", (_, _) => Run(() => Current()!.Selection.Invert()), _ => Current() is not null, shortcut: "Ctrl+I");
        Add(CommandIds.SelectByPattern, "Select by pattern", "Selection", (c, _) => Run(() =>
        {
            var matched = Current()!.Selection.SelectByPattern(c.Target!);
            shell.ShowMessage(matched == 1 ? "1 item matches" : $"{matched} items match");
        }), c => Current() is not null && !string.IsNullOrWhiteSpace(c.Target));
    }
}
