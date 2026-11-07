using Leviathan.Core.Model;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Windows;

/// <summary>
/// The words of the preview pane that depend on what is selected and where (spec §9). Pure, so the wording is tested; the renderers
/// of the next slice replace the placeholder with the preview itself.
/// </summary>
public static class PreviewText
{
    /// <summary>
    /// The pane's title: "Preview", and in a split tab which file pane it follows ("Preview · left pane"), because the pane previews
    /// the selection of whichever file pane has the focus (spec §9, §15.1).
    /// </summary>
    public static string Title(PaneLayout layout, int focusedPane) => layout switch
    {
        PaneLayout.SideBySide => focusedPane == 1 ? "Preview · right pane" : "Preview · left pane",
        PaneLayout.TopBottom => focusedPane == 1 ? "Preview · bottom pane" : "Preview · top pane",
        _ => "Preview",
    };

    /// <summary>What the video area says until a renderer fills it.</summary>
    public static string Placeholder(IReadOnlyList<FileEntry> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count == 0)
        {
            return "Select a file to preview it.";
        }

        if (selection.Count > 1)
        {
            return $"{selection.Count:N0} items selected";
        }

        var entry = selection[0];
        if (entry.Kind != FileEntryKind.File)
        {
            return $"{entry.Name}\nFolder";
        }

        return Leviathan.Media.MediaFileKinds.IsVideo(entry.Extension)
            ? $"{entry.Name}\nVideo. The player arrives in the next slice."
            : Leviathan.Media.MediaFileKinds.IsAudio(entry.Extension)
                ? $"{entry.Name}\nAudio. The player arrives in the next slice."
                : entry.Name;
    }
}
