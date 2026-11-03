using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Leviathan.App;

/// <summary>
/// The settings sheet in the window (spec 17.2, D36): Options (and Ctrl+,) raise it from the bottom over about 70% of the window,
/// dimming the rest; Esc or a click outside closes it; it can fill the window. The sheet is made the first time it is opened, so
/// startup does not pay for it. What it holds is in <see cref="SettingsSheet"/>; the rules of opening and closing are in
/// <see cref="SettingsSheetModel"/>.
/// </summary>
public sealed partial class MainWindow
{
    private SettingsSheet? _settingsSheet;

    private void InitSettingsSheet()
    {
        _viewModel.SettingsSheet.PropertyChanged += (_, _) => ApplySettingsPlacement();
        Root.SizeChanged += (_, _) => ApplySettingsPlacement();
    }

    private void OnOptionsClick(object sender, object e) => Run(CommandIds.SettingsOpen);

    private void OnSettingsScrimTapped(object sender, TappedRoutedEventArgs e) => _viewModel.SettingsSheet.Dismiss();

    private void ApplySettingsPlacement()
    {
        if (_closed)
        {
            return;
        }

        var model = _viewModel.SettingsSheet;
        if (!model.IsOpen)
        {
            SettingsLayer.Visibility = Visibility.Collapsed;
            return;
        }

        if (_settingsSheet is null)
        {
            _settingsSheet = new SettingsSheet(model) { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch };
            _settingsSheet.KeyRows = () => Leviathan.ViewModels.Commands.KeyMapBuilder.Rows(_commands.Commands, App.Host.Keys);
            _settingsSheet.DiagnosticsRequested += (_, _) => Run(CommandIds.AppDiagnostics);
            SettingsLayer.Children.Add(_settingsSheet);
        }

        SettingsLayer.Visibility = Visibility.Visible;
        if (model.IsExpanded)
        {
            _settingsSheet.VerticalAlignment = VerticalAlignment.Stretch;
            _settingsSheet.Height = double.NaN;
        }
        else
        {
            _settingsSheet.VerticalAlignment = VerticalAlignment.Bottom;
            _settingsSheet.Height = Math.Max(240, Root.ActualHeight * SettingsSheetModel.HeightFraction);
        }

        DispatcherQueue.TryEnqueue(() => _settingsSheet?.FocusSearch());
    }

    // Esc closes the sheet, or first clears the search text in it.
    private bool HandleSettingsEscape() => _viewModel.SettingsSheet.Dismiss();
}
