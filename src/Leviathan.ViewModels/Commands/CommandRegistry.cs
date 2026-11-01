using Leviathan.Diagnostics;

namespace Leviathan.ViewModels.Commands;

/// <summary>What happened when a command was asked to run.</summary>
public enum CommandResult
{
    /// <summary>The command ran to completion.</summary>
    Executed,

    /// <summary>No command has that id.</summary>
    Unknown,

    /// <summary>The command exists but is not enabled in this context.</summary>
    Disabled,
}

/// <summary>
/// Every user action is a command (build plan, Phase 1). The registry runs them, records each execution in the
/// <see cref="ActionTrail"/> so a crash bundle shows what the user did, and feeds the command palette (spec §6, §20.1).
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<CommandId, AppCommand> _commands = [];
    private readonly ActionTrail _trail;

    /// <summary>Creates a registry that records into <paramref name="trail"/>.</summary>
    public CommandRegistry(ActionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        _trail = trail;
    }

    /// <summary>All registered commands.</summary>
    public IReadOnlyCollection<AppCommand> Commands => _commands.Values;

    /// <summary>Adds a command. Registering the same id twice is a programming error.</summary>
    public void Register(AppCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_commands.TryAdd(command.Definition.Id, command))
        {
            throw new InvalidOperationException($"Command '{command.Definition.Id}' is already registered.");
        }
    }

    /// <summary>Finds a command by id.</summary>
    public bool TryGet(CommandId id, out AppCommand command) => _commands.TryGetValue(id, out command!);

    /// <summary>True when the command exists and is enabled in <paramref name="context"/>.</summary>
    public bool CanExecute(CommandId id, CommandContext? context = null) =>
        _commands.TryGetValue(id, out var command) && command.CanExecute(context ?? CommandContext.Empty);

    /// <summary>
    /// Runs a command. The execution is recorded before it starts, so a command that crashes is the last entry in the trail.
    /// Disabled and unknown commands do nothing and are not recorded.
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(CommandId id, CommandContext? context = null, CancellationToken cancellationToken = default)
    {
        context ??= CommandContext.Empty;
        if (!_commands.TryGetValue(id, out var command))
        {
            return CommandResult.Unknown;
        }

        if (!command.CanExecute(context))
        {
            return CommandResult.Disabled;
        }

        _trail.Record(id.Value, context.Target);
        await command.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        return CommandResult.Executed;
    }

    /// <summary>
    /// Palette search: every word of <paramref name="query"/> must appear in the title or category. Title-prefix matches
    /// rank first, then matches at the start of a word, then the rest; ties keep title order.
    /// </summary>
    public IReadOnlyList<AppCommand> Search(string query)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = new List<(AppCommand Command, int Score)>();
        foreach (var command in _commands.Values)
        {
            var score = Score(command.Definition, words);
            if (score >= 0)
            {
                scored.Add((command, score));
            }
        }

        return [.. scored.OrderBy(s => s.Score).ThenBy(s => s.Command.Definition.Title, StringComparer.OrdinalIgnoreCase).Select(s => s.Command)];
    }

    private static int Score(CommandDefinition definition, string[] words)
    {
        var title = definition.Title;
        var total = 0;
        foreach (var word in words)
        {
            var index = title.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (index == 0)
            {
                continue;
            }

            if (index > 0)
            {
                total += title[index - 1] == ' ' ? 1 : 2;
            }
            else if (definition.Category.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                total += 3;
            }
            else
            {
                return -1;
            }
        }

        return total;
    }
}
