namespace Leviathan.ViewModels.Commands;

/// <summary>Everything a command needs to run: an optional target and the UI's own parameter.</summary>
/// <param name="Target">Text target recorded in the action trail (usually a path); replay supplies only this.</param>
/// <param name="Parameter">Optional in-process argument that is not recorded.</param>
public sealed record CommandContext(string? Target = null, object? Parameter = null)
{
    /// <summary>A context with no target.</summary>
    public static CommandContext Empty { get; } = new();
}

/// <summary>Describes a command for menus, tooltips, the palette and key bindings (spec §6).</summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Title">Name shown in the palette and tooltips, e.g. "New tab".</param>
/// <param name="Category">Palette group, e.g. "Tabs".</param>
/// <param name="Icon">Optional icon key for the UI layer.</param>
/// <param name="DefaultShortcut">Default key gesture text such as <c>Ctrl+T</c>, or null.</param>
public sealed record CommandDefinition(CommandId Id, string Title, string Category, string? Icon = null, string? DefaultShortcut = null);

/// <summary>A runnable command. Every command takes a cancellation token because anything may touch the file system (CLAUDE.md).</summary>
public sealed class AppCommand
{
    private readonly Func<CommandContext, bool>? _canExecute;
    private readonly Func<CommandContext, CancellationToken, Task> _execute;

    /// <summary>Creates a command.</summary>
    public AppCommand(CommandDefinition definition, Func<CommandContext, CancellationToken, Task> execute, Func<CommandContext, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);
        Definition = definition;
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>The command's description.</summary>
    public CommandDefinition Definition { get; }

    /// <summary>True when the command can run in <paramref name="context"/>.</summary>
    public bool CanExecute(CommandContext context) => _canExecute?.Invoke(context) ?? true;

    internal Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken) => _execute(context, cancellationToken);
}
