using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Triage;

/// <summary>
/// Turns what the window reports about a key press (the Windows virtual-key code and which of Ctrl, Shift and Alt are down) into a
/// <see cref="KeyGesture"/>, so the key map never sees a framework type and the translation is tested without a keyboard. Keys with no
/// name here (media keys, the Windows key) give null, and the window leaves them to the system.
/// </summary>
public static class KeyNames
{
    /// <summary>The gesture for a virtual-key code with the modifiers held, or null for a key the map has no name for.</summary>
    public static KeyGesture? GestureFor(int virtualKey, bool ctrl, bool shift, bool alt)
    {
        var name = NameOf(virtualKey);
        if (name is null)
        {
            return null;
        }

        var modifiers = (ctrl ? KeyModifiers.Ctrl : 0) | (alt ? KeyModifiers.Alt : 0) | (shift ? KeyModifiers.Shift : 0);
        return KeyGesture.Create(modifiers, name);
    }

    /// <summary>True for the modifier keys themselves (Shift, Ctrl, Alt), which are never a gesture of their own.</summary>
    public static bool IsModifier(int virtualKey) => virtualKey is 16 or 17 or 18 or 160 or 161 or 162 or 163 or 164 or 165;

    private static string? NameOf(int vk) => vk switch
    {
        >= 65 and <= 90 => ((char)vk).ToString(),
        >= 48 and <= 57 => ((char)vk).ToString(),
        >= 96 and <= 105 => ((char)('0' + (vk - 96))).ToString(),
        >= 112 and <= 135 => "F" + (vk - 111),
        8 => "Backspace",
        9 => "Tab",
        13 => "Enter",
        27 => "Escape",
        32 => "Space",
        33 => "PageUp",
        34 => "PageDown",
        35 => "End",
        36 => "Home",
        37 => "Left",
        38 => "Up",
        39 => "Right",
        40 => "Down",
        45 => "Insert",
        46 => "Delete",
        186 => ";",
        187 or 107 => "=",
        188 => ",",
        189 or 109 => "-",
        190 or 110 => ".",
        191 => "/",
        192 => "`",
        219 => "[",
        220 => "\\",
        221 => "]",
        222 => "'",
        _ => null,
    };
}
