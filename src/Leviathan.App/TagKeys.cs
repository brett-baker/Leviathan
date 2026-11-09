using Leviathan.ViewModels.Folders;
using Windows.System;

namespace Leviathan.App;

/// <summary>Which keys are colour tag keys in the file view (spec §11.1): 1 to 9 toggle a tag, Ctrl+0 clears them.</summary>
internal static class TagKeys
{
    /// <summary>1 to 9 for a tag, 0 for clear, or null when the key is not a tag key now. A digit typed while a word is being typed for type-ahead belongs to the word, and nothing selected means nothing to tag.</summary>
    public static int? Of(VirtualKey key, bool ctrl, bool alt, SelectionModel selection)
    {
        if (alt || selection.SelectedEntries.Count == 0)
        {
            return null;
        }

        if (ctrl)
        {
            return key == VirtualKey.Number0 ? 0 : null;
        }

        return key is >= VirtualKey.Number1 and <= VirtualKey.Number9 && !selection.IsTypeAheadActive ? key - VirtualKey.Number0 : null;
    }
}
