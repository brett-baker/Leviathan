namespace Leviathan.ViewModels.Commands;

/// <summary>The key map the settings file asks for, and what was wrong with it.</summary>
/// <param name="Map">The default map with the user's changes applied.</param>
/// <param name="Problems">Entries that could not be used (an unknown command, text that is not a key), one sentence each.</param>
/// <param name="Notes">Changes worth knowing about: a key taken from another command.</param>
public sealed record KeyMapResult(KeyMap Map, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

/// <summary>One line of the Keyboard page: a command and the keys that run it.</summary>
/// <param name="Command">The command.</param>
/// <param name="Title">Its name.</param>
/// <param name="Category">Its group.</param>
/// <param name="Keys">The keys, e.g. <c>Ctrl+Y, Ctrl+Shift+Z</c>, or empty when it has none.</param>
/// <param name="IsCustom">True when the settings file changed this command's keys.</param>
public sealed record KeyboardRow(CommandId Command, string Title, string Category, string Keys, bool IsCustom);

/// <summary>
/// Applies the <c>KeyBindings</c> of the settings file to the default key map (spec §17.3: remappable through the settings file).
/// The file maps a command id to one gesture, e.g. <c>"edit.delete": "Ctrl+Backspace"</c>; that gesture then replaces every default
/// key of the command, an empty text removes its keys, and a key another command held goes to the one the file names. Entries are
/// applied in order of command id, so the result never depends on how the file is arranged. A bad entry is skipped and reported; it
/// never stops the others.
/// </summary>
public static class KeyMapBuilder
{
    /// <summary>Builds the map.</summary>
    /// <param name="overrides">The file's <c>KeyBindings</c>: command id to gesture text.</param>
    /// <param name="isKnownCommand">Tells whether a command id exists.</param>
    public static KeyMapResult Build(IReadOnlyDictionary<string, string>? overrides, Func<CommandId, bool> isKnownCommand)
    {
        ArgumentNullException.ThrowIfNull(isKnownCommand);
        var map = WindowCommands.DefaultKeyMap();
        var problems = new List<string>();
        var notes = new List<string>();
        foreach (var (id, text) in (overrides ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var command = new CommandId(id);
            if (!isKnownCommand(command))
            {
                problems.Add($"'{id}' is not a command, so its key was ignored.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                map.Unbind(command);
                notes.Add($"{id} has no key now.");
                continue;
            }

            if (!KeyGesture.TryParse(text, out var gesture))
            {
                problems.Add($"'{text}' is not a key for {id} (write it like Ctrl+Shift+K or F7), so the default stays.");
                continue;
            }

            map.Unbind(command);
            if (map.Replace(new KeyBinding(gesture, command)) is { } displaced && displaced != command)
            {
                notes.Add($"{gesture} now runs {id} instead of {displaced.Value}.");
            }
        }

        return new KeyMapResult(map, problems, notes);
    }

    /// <summary>The rows of the Keyboard page: every command with its keys, grouped by category.</summary>
    public static IReadOnlyList<KeyboardRow> Rows(IEnumerable<AppCommand> commands, KeyMap map)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(map);

        // "Changed" means the keys differ from the defaults: an entry that could not be used changes nothing.
        var defaults = WindowCommands.DefaultKeyMap();
        return
        [
            .. commands
                .Select(c => c.Definition)
                .Select(d => new KeyboardRow(
                    d.Id,
                    d.Title,
                    d.Category,
                    string.Join(", ", map.GesturesFor(d.Id)),
                    !map.GesturesFor(d.Id).SequenceEqual(defaults.GesturesFor(d.Id))))
                .OrderBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase),
        ];
    }
}
