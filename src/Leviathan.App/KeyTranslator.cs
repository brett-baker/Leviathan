using Leviathan.ViewModels.Commands;
using Microsoft.UI.Input;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace Leviathan.App;

/// <summary>Turns WinUI key presses into <see cref="KeyGesture"/>s for the key map.</summary>
internal static class KeyTranslator
{
    public static KeyGesture? ToGesture(VirtualKey key)
    {
        var name = NameOf(key);
        if (name is null)
        {
            return null;
        }

        var modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= KeyModifiers.Ctrl;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        return KeyGesture.Create(modifiers, name);
    }

    /// <summary>The number 1 to 9 for Ctrl+1..9 (tab shortcuts), or 0.</summary>
    public static int DigitOf(VirtualKey key) => key is >= VirtualKey.Number1 and <= VirtualKey.Number9 ? key - VirtualKey.Number0 : 0;

    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static string? NameOf(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => key.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)(key - VirtualKey.Number0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= VirtualKey.F1 and <= VirtualKey.F24 => key.ToString(),
        VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down => key.ToString(),
        VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Escape or VirtualKey.Delete or VirtualKey.Space or VirtualKey.Home or VirtualKey.End
            or VirtualKey.Insert or VirtualKey.PageUp or VirtualKey.PageDown => key.ToString(),
        VirtualKey.Back => "Backspace",
        (VirtualKey)186 => ";",
        (VirtualKey)187 => "=",
        (VirtualKey)188 => ",",
        (VirtualKey)189 => "-",
        (VirtualKey)190 => ".",
        (VirtualKey)191 => "/",
        (VirtualKey)192 => "`",
        (VirtualKey)219 => "[",
        (VirtualKey)220 => "\\",
        (VirtualKey)221 => "]",
        (VirtualKey)222 => "'",
        _ => null,
    };
}
