using Leviathan.Core.Model;
using Microsoft.UI.Xaml;

namespace Leviathan.App;

/// <summary>The preview pane's folder-size lines (spec §12.7); the preview itself arrives in Phase 3.</summary>
public sealed partial class MainWindow
{
    /// <summary>Shows the size of the one selected folder, and asks for it to be counted when it is not known.</summary>
    private void UpdatePreview()
    {
        if (_bound?.Selection.SelectedEntries is not { Count: 1 } selected || selected[0].Kind == FileEntryKind.File || _bound.Sizes is not { } sizes)
        {
            PreviewName.Visibility = Visibility.Collapsed;
            PreviewSize.Visibility = Visibility.Collapsed;
            return;
        }

        var folder = selected[0];
        sizes.RequestNow(folder);
        PreviewName.Text = folder.Name;
        PreviewName.Visibility = Visibility.Visible;
        var detail = sizes.DetailFor(folder.Location);
        PreviewSize.Text = detail.Length > 0 ? detail : sizes.TextFor(folder).Length > 0 ? "Counting…" : string.Empty;
        PreviewSize.Visibility = PreviewSize.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // A total arrived for a folder the tab asked about: the pane and the card show it.
    private void OnFolderSizesChanged(object? sender, IReadOnlyList<Core.Paths.LocationPath> folders) => OnUi(() =>
    {
        UpdatePreview();
        UpdateHeaders();
    });
}
