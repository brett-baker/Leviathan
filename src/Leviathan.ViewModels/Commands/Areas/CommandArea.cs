// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Paths;
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

    /// <summary>Registers a command that does its work synchronously and does not need the call's context.</summary>
    protected void AddAction(CommandId id, string title, string category, Action run, Func<CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
        Add(id, title, category, (_, _) => Run(run), can, icon, shortcut);

    /// <summary>Registers a command that does its work synchronously, given the call's context (target and parameter).</summary>
    protected void AddAction(CommandId id, string title, string category, Action<CommandContext> run, Func<CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
        Add(id, title, category, (c, _) => Run(() => run(c)), can, icon, shortcut);

    /// <summary>
    /// Registers a command that works on the pane the user is in. It is enabled only while there is one, and <paramref name="can"/> adds
    /// to that; <paramref name="run"/> is handed the tab, so it never has to look for it (or assume it is there).
    /// </summary>
    protected void AddOnCurrent(CommandId id, string title, string category, Action<TabViewModel, CommandContext> run, Func<TabViewModel, CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
        AddAction(id, title, category, c => run(Current()!, c), c => Current() is { } tab && (can?.Invoke(tab, c) ?? true), icon, shortcut);

    /// <summary>The asynchronous form of <see cref="AddOnCurrent(CommandId, string, string, Action{TabViewModel, CommandContext}, Func{TabViewModel, CommandContext, bool}?, string?, string?)"/>.</summary>
    protected void AddOnCurrent(CommandId id, string title, string category, Func<TabViewModel, CommandContext, CancellationToken, Task> run, Func<TabViewModel, CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
        Add(id, title, category, (c, ct) => run(Current()!, c, ct), c => Current() is { } tab && (can?.Invoke(tab, c) ?? true), icon, shortcut);

    /// <summary>
    /// Registers a command about a tab: the one handed over as the parameter, else the selected one (see <see cref="TabOf"/>). Enabled only
    /// when there is such a tab, and <paramref name="can"/> adds to that.
    /// </summary>
    protected void AddOnTab(CommandId id, string title, string category, Action<TabViewModel, CommandContext> run, Func<TabViewModel, CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
        AddAction(id, title, category, c => run(TabOf(c)!, c), c => TabOf(c) is { } tab && (can?.Invoke(tab, c) ?? true), icon, shortcut);

    /// <summary>The asynchronous form of <see cref="AddOnTab(CommandId, string, string, Action{TabViewModel, CommandContext}, Func{TabViewModel, CommandContext, bool}?, string?, string?)"/>.</summary>
    protected void AddOnTab(CommandId id, string title, string category, Func<TabViewModel, CommandContext, CancellationToken, Task> run, Func<TabViewModel, CommandContext, bool>? can = null, string? icon = null, string? shortcut = null) =>
        Add(id, title, category, (c, ct) => run(TabOf(c)!, c, ct), c => TabOf(c) is { } tab && (can?.Invoke(tab, c) ?? true), icon, shortcut);

    /// <summary>Registers a command whose parameter is a <typeparamref name="TParameter"/> (a drop, a move): it is enabled only when the parameter is one and <paramref name="can"/> agrees, so <paramref name="run"/> needs no cast.</summary>
    protected void AddWith<TParameter>(CommandId id, string title, string category, Action<TParameter> run, Func<TParameter, bool>? can = null, string? icon = null, string? shortcut = null)
        where TParameter : notnull =>
        AddAction(id, title, category, c => run((TParameter)c.Parameter!), c => c.Parameter is TParameter p && (can?.Invoke(p) ?? true), icon, shortcut);

    /// <summary>The asynchronous form of <see cref="AddWith{TParameter}(CommandId, string, string, Action{TParameter}, Func{TParameter, bool}?, string?, string?)"/>.</summary>
    protected void AddWith<TParameter>(CommandId id, string title, string category, Func<TParameter, CancellationToken, Task> run, Func<TParameter, bool>? can = null, string? icon = null, string? shortcut = null)
        where TParameter : notnull =>
        Add(id, title, category, (c, ct) => run((TParameter)c.Parameter!, ct), c => c.Parameter is TParameter p && (can?.Invoke(p) ?? true), icon, shortcut);

    /// <summary>Registers a command on the current pane whose parameter is a <typeparamref name="TParameter"/>: enabled only with a pane and a parameter of that type (and when <paramref name="can"/> agrees), so <paramref name="run"/> needs no cast.</summary>
    protected void AddOnCurrentWith<TParameter>(CommandId id, string title, string category, Action<TabViewModel, TParameter> run, Func<TabViewModel, TParameter, bool>? can = null, string? icon = null, string? shortcut = null)
        where TParameter : notnull =>
        AddAction(id, title, category, c => run(Current()!, (TParameter)c.Parameter!), c => Current() is { } tab && c.Parameter is TParameter p && (can?.Invoke(tab, p) ?? true), icon, shortcut);

    /// <summary>
    /// Registers a command that needs an optional service (favorites, profiles...): it is disabled while the service is absent, and
    /// <paramref name="run"/> is handed the service, so it never has to assume it is there (docs/simplify/window-commands.md, item 7).
    /// </summary>
    protected void AddUsing<TService>(TService? service, CommandId id, string title, string category, Action<TService, CommandContext> run, Func<TService, CommandContext, bool>? can = null, string? icon = null, string? shortcut = null)
        where TService : class =>
        AddAction(id, title, category, c => run(service!, c), c => service is not null && (can?.Invoke(service, c) ?? true), icon, shortcut);

    /// <summary>The asynchronous form of <c>AddUsing</c>.</summary>
    protected void AddUsing<TService>(TService? service, CommandId id, string title, string category, Func<TService, CommandContext, CancellationToken, Task> run, Func<TService, CommandContext, bool>? can = null, string? icon = null, string? shortcut = null)
        where TService : class =>
        Add(id, title, category, (c, ct) => run(service!, c, ct), c => service is not null && (can?.Invoke(service, c) ?? true), icon, shortcut);

    /// <summary>
    /// Judges a move or copy with the drop rules (a refusal says why in the status bar), then queues it as the same journaled transfer a drop
    /// makes (spec §8.4). Used by drops, the quick targets and the other-pane keys.
    /// </summary>
    protected async Task TransferAsync(IReadOnlyList<LocationPath> sources, LocationPath destination, Transfers.DropModifiers modifiers, CancellationToken cancellationToken)
    {
        var decision = Transfers.DropRules.Decide(sources, destination, modifiers);
        if (!decision.Allowed)
        {
            Scope.Shell.ShowMessage(decision.Tooltip);
            return;
        }

        var outcome = await (Scope.TransferService ?? new Transfers.UnavailableTransferService())
            .StartAsync(new Transfers.TransferRequest(sources, destination, decision.Mode), cancellationToken);
        Scope.Shell.ShowMessage(outcome.Message);
    }

    /// <summary>The modifier keys a drop would hold for this mode: Ctrl copies, Shift moves.</summary>
    protected static Transfers.DropModifiers ModifiersFor(Transfers.TransferMode mode) =>
        mode == Transfers.TransferMode.Copy ? Transfers.DropModifiers.Ctrl : Transfers.DropModifiers.Shift;

    /// <summary>A can-execute for commands that only need a pane to work on.</summary>
    protected bool NeedsTab(CommandContext context) => Current() is not null;

    /// <summary>How many steps a back or forward command goes: the integer parameter, else one.</summary>
    protected static int Steps(CommandContext context) => context.Parameter is int steps and > 0 ? steps : 1;

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
