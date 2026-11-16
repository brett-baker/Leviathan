using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>The base of a command area (one file of related commands): shared helpers and the scope it registers against.</summary>
internal abstract class CommandArea(CommandScope scope)
{
    /// <summary>What the area registers against.</summary>
    protected CommandScope Scope { get; } = scope;

    /// <summary>The pane the user is working in: the focused one of a split tab (spec §15.1). Tab-level commands (close, pin, duplicate) use <see cref="TabOf"/>.</summary>
    protected TabViewModel? Current() => Scope.Strip.Selected?.Focused;

    /// <summary>The tab a command is about: the one handed over as its parameter, else the selected tab.</summary>
    protected TabViewModel? TabOf(CommandContext c) => c.Parameter as TabViewModel ?? Scope.Strip.Selected;

    /// <summary>Registers one command.</summary>
    protected void Add(CommandId id, string title, string category, Func<CommandContext, CancellationToken, Task> run, Func<CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
        Scope.Registry.Register(new AppCommand(new CommandDefinition(id, title, category, icon, shortcut), run, can));

    /// <summary>How many steps a back or forward command goes: the integer parameter, else one.</summary>
    protected static int Steps(CommandContext context) => context.Parameter is int steps and > 0 ? steps : 1;

    /// <summary>Runs a synchronous action as a completed task.</summary>
    protected static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
