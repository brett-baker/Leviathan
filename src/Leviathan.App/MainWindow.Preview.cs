using Leviathan.Core.Model;
using Leviathan.ViewModels.Commands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>The preview pane's folder-size lines (spec §12.7); the preview itself arrives in Phase 3.</summary>
public sealed partial class MainWindow
{
    /// <summary>Shows the size of the one selected folder, and asks for it to be counted when it is not known.</summary>
    private void UpdatePreview()
    {
        UpdatePreviewActions();
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

    // Move and Copy under the preview send the focused pane's selection to the other pane's folder, like F6 and F5.
    private void UpdatePreviewActions()
    {
        var tab = _viewModel.Tabs.Selected;
        var split = tab is { IsSplit: true };
        PreviewActions.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        if (!split)
        {
            return;
        }

        PreviewMoveButton.IsEnabled = _commands.CanExecute(CommandIds.PaneMoveToOther, new CommandContext(null));
        PreviewCopyButton.IsEnabled = _commands.CanExecute(CommandIds.PaneCopyToOther, new CommandContext(null));
        var target = tab!.Other?.History.Current.ToString() ?? string.Empty;
        ToolTipService.SetToolTip(PreviewMoveButton, $"Move the selection to {target}");
        ToolTipService.SetToolTip(PreviewCopyButton, $"Copy the selection to {target}");
    }

    private void OnPreviewMoveClick(object sender, RoutedEventArgs e) => Run(CommandIds.PaneMoveToOther);

    private void OnPreviewCopyClick(object sender, RoutedEventArgs e) => Run(CommandIds.PaneCopyToOther);

    // A total arrived for a folder the tab asked about: the pane and the card show it.
    private void OnFolderSizesChanged(object? sender, IReadOnlyList<Core.Paths.LocationPath> folders) => OnUi(() =>
    {
        UpdatePreview();
        UpdateHeaders();
    });
}
