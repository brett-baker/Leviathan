using System.Text.Json;

namespace Leviathan.ViewModels.Commands;

/// <summary>What reading an exported key file gave: the bindings that were usable and one sentence for each entry that was not.</summary>
/// <param name="Bindings">Command id to gesture text, only entries that name a known command and a real key (or an empty text, which removes the keys).</param>
/// <param name="Problems">Entries skipped, and a note when the file is not a key file at all.</param>
/// <param name="IsKeyFile">False when the text is not a JSON object of texts; nothing should be imported then.</param>
public sealed record KeyImport(IReadOnlyDictionary<string, string> Bindings, IReadOnlyList<string> Problems, bool IsKeyFile);

/// <summary>
/// The edits the Keyboard page makes to the user's key bindings (spec §17.2, §17.3). Only the user's changes are stored: a key that
/// equals the default is not written, so resetting a command is deleting its entry and a later release can move the defaults. Pure
/// functions over the <c>KeyBindings</c> dictionary, so every rule is unit-tested without the page.
/// </summary>
public static class KeyBindingEditor
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>The other command that holds <paramref name="gesture"/> in <paramref name="map"/>, or null when it is free (or held by <paramref name="command"/> itself).</summary>
    public static CommandId? HeldBy(KeyMap map, CommandId command, KeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.Bindings.FirstOrDefault(b => b.Gesture == gesture && b.Scope == KeyScope.Always && b.Command != command)?.Command;
    }

    /// <summary>
    /// The bindings with <paramref name="command"/> given <paramref name="gesture"/> as its only key. When that is what the defaults
    /// already give it, the entry is dropped instead.
    /// </summary>
    /// <param name="bindings">The current bindings.</param>
    /// <param name="command">The command.</param>
    /// <param name="gesture">The new key.</param>
    /// <param name="defaults">The default map of the command's scope (the file view's, or triage's).</param>
    public static Dictionary<string, string> Set(IReadOnlyDictionary<string, string> bindings, CommandId command, KeyGesture gesture, KeyMap defaults)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(defaults);
        var result = new Dictionary<string, string>(bindings);
        var same = defaults.GesturesFor(command);
        if (same.Count == 1 && same[0] == gesture)
        {
            result.Remove(command.Value);
        }
        else
        {
            result[command.Value] = gesture.ToString();
        }

        return result;
    }

    /// <summary>The bindings with the keys of <paramref name="command"/> removed (an empty text, which the key map builder reads as "no key").</summary>
    public static Dictionary<string, string> Unbind(IReadOnlyDictionary<string, string> bindings, CommandId command, KeyMap defaults)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(defaults);
        var result = new Dictionary<string, string>(bindings);
        if (defaults.GesturesFor(command).Count == 0)
        {
            result.Remove(command.Value); // it has no default key either: nothing to say
        }
        else
        {
            result[command.Value] = string.Empty;
        }

        return result;
    }

    /// <summary>The bindings without the entry of <paramref name="command"/>: its default keys again.</summary>
    public static Dictionary<string, string> Reset(IReadOnlyDictionary<string, string> bindings, CommandId command)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var result = new Dictionary<string, string>(bindings);
        result.Remove(command.Value);
        return result;
    }

    /// <summary>The user's bindings as a small JSON file to keep or share.</summary>
    public static string Export(IReadOnlyDictionary<string, string> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return JsonSerializer.Serialize(bindings.OrderBy(b => b.Key, StringComparer.Ordinal).ToDictionary(b => b.Key, b => b.Value), Pretty);
    }

    /// <summary>
    /// Reads a file written by <see cref="Export"/>. Entries naming a command this version does not have, or text that is not a key,
    /// are left out and reported; the rest is usable.
    /// </summary>
    public static KeyImport Import(string? json, Func<CommandId, bool> isKnownCommand)
    {
        ArgumentNullException.ThrowIfNull(isKnownCommand);
        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? string.Empty);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
        {
            return new KeyImport(new Dictionary<string, string>(), ["That file is not a Leviathan key file."], IsKeyFile: false);
        }

        var good = new Dictionary<string, string>();
        var problems = new List<string>();
        foreach (var (id, text) in parsed.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!isKnownCommand(new CommandId(id)))
            {
                problems.Add($"'{id}' is not a command of this version, so it was left out.");
            }
            else if (!string.IsNullOrWhiteSpace(text) && !KeyGesture.TryParse(text, out _))
            {
                problems.Add($"'{text}' is not a key for {id}, so it was left out.");
            }
            else
            {
                good[id] = text ?? string.Empty;
            }
        }

        return new KeyImport(good, problems, IsKeyFile: true);
    }
}
