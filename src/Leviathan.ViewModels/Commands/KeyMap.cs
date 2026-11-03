namespace Leviathan.ViewModels.Commands;

/// <summary>When a binding is active.</summary>
public enum KeyScope
{
    /// <summary>Always.</summary>
    Always,

    /// <summary>Only while the tab is split into two panes (spec §17.3: F5 and F6 mean copy/move to the other pane).</summary>
    SplitOnly,
}

/// <summary>One key binding.</summary>
/// <param name="Gesture">The keys.</param>
/// <param name="Command">The command they run.</param>
/// <param name="Scope">When the binding is active.</param>
public sealed record KeyBinding(KeyGesture Gesture, CommandId Command, KeyScope Scope = KeyScope.Always);

/// <summary>
/// Maps key gestures to commands. A gesture may have a single-pane and a split-pane meaning; while split, the split meaning
/// wins unless <see cref="SplitBindingsWin"/> is turned off (spec §17.3).
/// </summary>
public sealed class KeyMap
{
    private readonly List<KeyBinding> _bindings = [];

    /// <summary>True (default) when a <see cref="KeyScope.SplitOnly"/> binding overrides a plain one while split.</summary>
    public bool SplitBindingsWin { get; set; } = true;

    /// <summary>All bindings.</summary>
    public IReadOnlyList<KeyBinding> Bindings => _bindings;

    /// <summary>
    /// Adds a binding. Returns the command already holding the same gesture in the same scope, or null when added.
    /// A gesture can be bound once as <see cref="KeyScope.Always"/> and once as <see cref="KeyScope.SplitOnly"/>.
    /// </summary>
    public CommandId? TryBind(KeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var existing = _bindings.FirstOrDefault(b => b.Gesture == binding.Gesture && b.Scope == binding.Scope);
        if (existing is not null)
        {
            return existing.Command;
        }

        _bindings.Add(binding);
        return null;
    }

    /// <summary>
    /// Adds a binding, taking the gesture from whichever command held it in the same scope. Returns that command, or null when the
    /// gesture was free.
    /// </summary>
    public CommandId? Replace(KeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var existing = _bindings.FirstOrDefault(b => b.Gesture == binding.Gesture && b.Scope == binding.Scope);
        if (existing is not null)
        {
            _bindings.Remove(existing);
        }

        _bindings.Add(binding);
        return existing?.Command;
    }

    /// <summary>Removes every binding of <paramref name="command"/> (used before a rebind).</summary>
    public int Unbind(CommandId command) => _bindings.RemoveAll(b => b.Command == command);

    /// <summary>The gestures bound to a command, for tooltips ("Copy (Ctrl+C)"). Plain bindings first.</summary>
    public IReadOnlyList<KeyGesture> GesturesFor(CommandId command) =>
        [.. _bindings.Where(b => b.Command == command).OrderBy(b => b.Scope).Select(b => b.Gesture)];

    /// <summary>Finds the command for a pressed gesture, or null.</summary>
    public CommandId? Resolve(KeyGesture gesture, bool isSplit)
    {
        var plain = _bindings.FirstOrDefault(b => b.Gesture == gesture && b.Scope == KeyScope.Always);
        var split = isSplit ? _bindings.FirstOrDefault(b => b.Gesture == gesture && b.Scope == KeyScope.SplitOnly) : null;

        if (split is not null && (SplitBindingsWin || plain is null))
        {
            return split.Command;
        }

        return plain?.Command;
    }
}
