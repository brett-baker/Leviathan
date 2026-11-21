using System.ComponentModel;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sidebar;
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

    private SidebarTab _sidebarTab = SidebarTab.Files;

    private void OnFilesTabClick(object sender, RoutedEventArgs e) => ChooseSidebarTab(SidebarTab.Files);

    private void OnFavoritesTabClick(object sender, RoutedEventArgs e) => ChooseSidebarTab(SidebarTab.Favorites);

    // The tab under the tree decides which sections the tree shows (spec §7); the same tab again just stays checked.
    private void ChooseSidebarTab(SidebarTab tab)
    {
        ShowSidebarTab(tab);
        if (_sidebarTab == tab)
        {
            return;
        }

        _sidebarTab = tab;
        App.Actions.Record("sidebar.tab", tab.ToString());
        RebuildSidebar();
    }

    // The active tab is a tab shape that joins the panel above it (open at the top, lined on the other sides, label in the text colour); the other
    // one is plain and grey. This is the look of Visual Studio's Solution Explorer tabs.
    private void ShowSidebarTab(SidebarTab tab)
    {
        Style(FilesTabFrame, FilesTabLabel, tab == SidebarTab.Files);
        Style(FavoritesTabFrame, FavoritesTabLabel, tab == SidebarTab.Favorites);

        static void Style(Border frame, TextBlock label, bool active)
        {
            var resources = Application.Current.Resources;
            frame.BorderThickness = active ? new Thickness(1, 0, 1, 1) : new Thickness(0);
            frame.Background = active
                ? (Microsoft.UI.Xaml.Media.Brush)resources["LayerFillColorDefaultBrush"]
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            label.Foreground = (Microsoft.UI.Xaml.Media.Brush)resources[active ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        }
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
        SidebarColumnHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(SidebarGrip, collapsed ? "Double-click or drag to show the sidebar (Ctrl+B)" : "Drag to resize the sidebar. Double-click to hide it (Ctrl+B).");
    }
}
