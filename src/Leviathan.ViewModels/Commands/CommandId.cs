namespace Leviathan.ViewModels.Commands;

/// <summary>Stable identifier of a command, e.g. <c>tab.new</c>. Recorded in the action trail and used by key bindings (spec §6, §20.1).</summary>
public readonly record struct CommandId
{
    /// <summary>Creates an id; ids are lower-case dotted names.</summary>
    public CommandId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>The id text.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
