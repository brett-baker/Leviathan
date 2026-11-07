using Leviathan.Core.Model;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The preview and details pane (spec §9): its width and splitter, the S / M / L video size and the handle under the video, the two
/// collapsible parts, the folder-size lines (spec §12.7), and the floating quick preview. The window's view model holds the sizes (so
/// they are saved in sessions); this file connects them to the controls, within <see cref="PreviewLayout"/>'s limits. The renderers
/// that fill the video area arrive in the next slice.
/// </summary>
public sealed partial class MainWindow
{
    private double _handleStartY;
    private double _handleStartHeight;
    private bool _handleDragging;

    private void InitializePreviewPane()
    {
        PreviewGrip.GrowsLeft = true;
        PreviewGrip.CurrentWidth = () => _viewModel.ShowPreview ? _viewModel.PreviewWidth : 0;
        PreviewGrip.WidthRequested += (_, width) =>
        {
            // Dragging the pane narrower than half its minimum hides it, like the sidebar.
            if (width < PreviewLayout.MinWidth / 2)
            {
                _viewModel.ShowPreview = false;
                return;
            }

            _viewModel.PreviewWidth = PreviewLayout.Clamp(width, WindowWidth());
        };
        PreviewGrip.ToggleRequested += (_, _) => Run(CommandIds.PreviewPane);
        PreviewGrip.ResetRequested += (_, _) => _viewModel.PreviewWidth = PreviewLayout.DefaultWidth;
        ToolTipService.SetToolTip(PreviewGrip, "Drag to resize the preview pane. Double-click to hide it (Alt+P).");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PreviewGrip, "Resize preview pane");
        VideoHandle.PointerPressed += OnVideoHandlePressed;
        VideoHandle.PointerMoved += OnVideoHandleMoved;
        VideoHandle.PointerReleased += (_, e) => EndVideoHandleDrag(e.Pointer);
        VideoHandle.PointerCaptureLost += (_, _) => EndVideoHandleDrag(null);
        FileList.QuickPreviewRequested += OnQuickPreviewRequested;
        FileList2.QuickPreviewRequested += OnQuickPreviewRequested;
        FlowList.QuickPreviewRequested += OnQuickPreviewRequested;
        FlowList2.QuickPreviewRequested += OnQuickPreviewRequested;
        SizeChanged += (_, _) => ApplyPreviewPane();
    }

    /// <summary>Shows or hides the pane and gives its parts the saved width, video height and collapsed state.</summary>
    private void ApplyPreviewPane()
    {
        var shown = _viewModel.ShowPreview;
        PreviewPane.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PreviewGrip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(shown ? PreviewLayout.Clamp(_viewModel.PreviewWidth, WindowWidth()) : 0);
        PreviewToggle.IsChecked = shown;

        VideoArea.Height = _viewModel.CurrentVideoHeight;
        VideoSmallButton.IsChecked = _viewModel.VideoSize == VideoSize.Small;
        VideoMediumButton.IsChecked = _viewModel.VideoSize == VideoSize.Medium;
        VideoLargeButton.IsChecked = _viewModel.VideoSize == VideoSize.Large;

        PreviewBody.Visibility = _viewModel.PreviewCollapsed ? Visibility.Collapsed : Visibility.Visible;
        PreviewSectionLabel.Text = (_viewModel.PreviewCollapsed ? "▸ " : "▾ ") + "Preview";
        DetailsBody.Visibility = _viewModel.DetailsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        DetailsSectionLabel.Text = (_viewModel.DetailsCollapsed ? "▸ " : "▾ ") + "Details";
    }

    private void OnQuickPreviewRequested(object? sender, EventArgs e) => Run(CommandIds.PreviewQuick);

    private void OnVideoSmallClick(object sender, RoutedEventArgs e) => ChooseVideoSize(sender, CommandIds.PreviewVideoSmall);

    private void OnVideoMediumClick(object sender, RoutedEventArgs e) => ChooseVideoSize(sender, CommandIds.PreviewVideoMedium);

    private void OnVideoLargeClick(object sender, RoutedEventArgs e) => ChooseVideoSize(sender, CommandIds.PreviewVideoLarge);

    // Clicking the button that is already on would switch it off; the size stays, so the button stays on.
    private void ChooseVideoSize(object sender, CommandId command)
    {
        ((Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)sender).IsChecked = true;
        Run(command);
    }

    private void OnPreviewSectionClick(object sender, RoutedEventArgs e) => _viewModel.PreviewCollapsed = !_viewModel.PreviewCollapsed;

    private void OnDetailsSectionClick(object sender, RoutedEventArgs e) => _viewModel.DetailsCollapsed = !_viewModel.DetailsCollapsed;

    private void OnVideoHandlePressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(VideoHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _handleDragging = true;
        _handleStartY = e.GetCurrentPoint(null).Position.Y;
        _handleStartHeight = _viewModel.CurrentVideoHeight;
        VideoHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnVideoHandleMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_handleDragging)
        {
            _viewModel.DragVideoHeight(_handleStartHeight + (e.GetCurrentPoint(null).Position.Y - _handleStartY));
            e.Handled = true;
        }
    }

    private void EndVideoHandleDrag(Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        if (!_handleDragging)
        {
            return;
        }

        _handleDragging = false;
        if (pointer is not null)
        {
            VideoHandle.ReleasePointerCapture(pointer);
        }
    }

    /// <summary>Shows the size of the one selected folder, and asks for it to be counted when it is not known.</summary>
    private void UpdatePreview()
    {
        UpdatePreviewActions();
        var tab = _viewModel.Tabs.Selected;
        PreviewTitle.Text = PreviewText.Title(tab is { IsSplit: true } ? tab.Panes : ViewModels.Tabs.PaneLayout.Single, tab?.FocusedPane ?? 0);
        VideoPlaceholder.Text = PreviewText.Placeholder(_bound?.Selection.SelectedEntries ?? []);
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

    private QuickPreviewWindow? _quick;

    /// <summary>
    /// Space (spec §9): the large floating preview of the selection, or of the focused item when nothing is selected; the same key
    /// closes it. It follows the focused pane's selection like the pane does.
    /// </summary>
    public void ToggleQuickPreview() => OnUi(() =>
    {
        if (_quick is not null)
        {
            _quick.Close();
            return;
        }

        var selection = _bound?.Selection;
        IReadOnlyList<FileEntry> entries = selection?.SelectedEntries is { Count: > 0 } chosen
            ? chosen
            : selection?.Focus is { } focus && _bound!.Items.Rows.FirstOrDefault(r => r.Location == focus) is { } focused ? [focused] : [];
        if (entries.Count == 0)
        {
            return;
        }

        _quick = new QuickPreviewWindow(entries);
        _quick.Closed += (_, _) => _quick = null;
        _quick.Activate();
    });
}
