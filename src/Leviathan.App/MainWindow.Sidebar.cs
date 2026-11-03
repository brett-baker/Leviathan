using System.ComponentModel;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The sidebar's width and collapse state (spec §4). The window's view model holds them (so they are saved in sessions and
/// profiles); this file connects them to the splitter and the grid column, within <see cref="SidebarLayout"/>'s limits.
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeSidebarSplitter()
    {
        SidebarGrip.CurrentWidth = () => _viewModel.SidebarCollapsed ? 0 : _viewModel.SidebarWidth;
        SidebarGrip.WidthRequested += (_, width) =>
        {
            // Dragging out of a collapsed sidebar brings it back at the width the pointer is at.
            var clamped = SidebarLayout.Clamp(width, WindowWidth());
            if (width < SidebarLayout.MinWidth / 2 && !_viewModel.SidebarCollapsed)
            {
                _viewModel.SidebarCollapsed = true;
                return;
            }

            _viewModel.SidebarCollapsed = false;
            _viewModel.SidebarWidth = clamped;
        };
        SidebarGrip.ToggleRequested += (_, _) => Run(CommandIds.ViewSidebar);
        SidebarGrip.ResetRequested += (_, _) =>
        {
            _viewModel.SidebarCollapsed = false;
            _viewModel.SidebarWidth = SidebarLayout.DefaultWidth;
        };
        _viewModel.PropertyChanged += OnSidebarPropertyChanged;
        SizeChanged += (_, _) => ApplySidebar();
        ApplySidebar();
    }

    private double WindowWidth() => Content is FrameworkElement { ActualWidth: > 0 } root ? root.ActualWidth : SidebarLayout.MaxWidth * 2;

    private void OnSidebarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WindowViewModel.SidebarWidth) or nameof(WindowViewModel.SidebarCollapsed))
        {
            OnUi(ApplySidebar);
        }
    }

    private void ApplySidebar()
    {
        var collapsed = _viewModel.SidebarCollapsed;
        SidebarColumn.Width = new GridLength(collapsed ? 0 : SidebarLayout.Clamp(_viewModel.SidebarWidth, WindowWidth()));
        Sidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(SidebarGrip, collapsed ? "Double-click or drag to show the sidebar (Ctrl+B)" : "Drag to resize the sidebar. Double-click to hide it (Ctrl+B).");
    }
}
