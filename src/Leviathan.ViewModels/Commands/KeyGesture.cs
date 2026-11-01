using System.Diagnostics.CodeAnalysis;

namespace Leviathan.ViewModels.Commands;

/// <summary>Modifier keys of a gesture.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Ctrl.</summary>
    Ctrl = 1,

    /// <summary>Alt.</summary>
    Alt = 2,

    /// <summary>Shift.</summary>
    Shift = 4,
}

/// <summary>
/// A key plus modifiers in a canonical text form (<c>Ctrl+Alt+Shift+Key</c>) so bindings compare and serialize the same way
/// whatever the user typed in the settings file (spec §17.3).
/// </summary>
public readonly record struct KeyGesture
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Del"] = "Delete",
        ["Esc"] = "Escape",
        ["Return"] = "Enter",
        ["PgUp"] = "PageUp",
        ["PgDn"] = "PageDown",
        ["Ins"] = "Insert",
        ["Back"] = "Backspace",
        ["Spacebar"] = "Space",
    };

    private KeyGesture(KeyModifiers modifiers, string key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    /// <summary>The modifiers.</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>The canonical key name: a letter or digit in upper case, a named key such as <c>F5</c> or <c>PageUp</c>, or a symbol.</summary>
    public string Key { get; }

    /// <summary>Creates a gesture from parts; the key is canonicalized.</summary>
    public static KeyGesture Create(KeyModifiers modifiers, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new KeyGesture(modifiers, CanonicalKey(key));
    }

    /// <summary>Parses text such as <c>Ctrl+Shift+P</c>, <c>alt+left</c> or <c>Ctrl++</c>.</summary>
    public static KeyGesture Parse(string text) =>
        TryParse(text, out var gesture) ? gesture : throw new FormatException($"'{text}' is not a key gesture.");

    /// <summary>Parses gesture text; returns false for empty text, unknown modifiers or a missing key.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        string[] parts;
        if (value == "+")
        {
            parts = ["+"];
        }
        else if (value.EndsWith("++", StringComparison.Ordinal))
        {
            // The key itself is '+': everything before the final '+' is modifiers.
            parts = [.. value[..^2].Split('+', StringSplitOptions.TrimEntries), "+"];
        }
        else
        {
            parts = value.Split('+', StringSplitOptions.TrimEntries);
        }

        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= KeyModifiers.Ctrl;
                    break;
                case "ALT":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= KeyModifiers.Shift;
                    break;
                default:
                    return false;
            }
        }

        var key = parts[^1];
        if (key.Length == 0)
        {
            return false;
        }

        gesture = new KeyGesture(modifiers, CanonicalKey(key));
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    private static string CanonicalKey(string key)
    {
        if (Aliases.TryGetValue(key, out var alias))
        {
            return alias;
        }

        if (key.Length == 1)
        {
            return key.ToUpperInvariant();
        }

        // Function keys keep their F; other named keys get an initial capital (Left, PageUp).
        if (key.Length is 2 or 3 && (key[0] is 'f' or 'F') && key[1..].All(char.IsDigit))
        {
            return "F" + key[1..];
        }

        return char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant();
    }
}
