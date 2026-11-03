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

        // Building the sheet takes about 60 ms, which would be a stall at the moment the user asks for settings. It is built a moment
        // after the window is up, while nothing is happening (the one stall moves to idle time).
        Root.Loaded += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => EnsureSettingsSheet());
        };
    }

    private SettingsSheet? EnsureSettingsSheet()
    {
        if (_settingsSheet is null && !_closed)
        {
            _settingsSheet = new SettingsSheet(_viewModel.SettingsSheet) { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch };
            _settingsSheet.KeyRows = () => Leviathan.ViewModels.Commands.KeyMapBuilder.Rows(_commands.Commands, App.Host.Keys);
            _settingsSheet.DiagnosticsRequested += (_, _) => Run(CommandIds.AppDiagnostics);
            SettingsLayer.Children.Add(_settingsSheet);
        }

        return _settingsSheet;
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

        if (EnsureSettingsSheet() is not { } sheet)
        {
            return;
        }

        SettingsLayer.Visibility = Visibility.Visible;
        if (model.IsExpanded)
        {
            sheet.VerticalAlignment = VerticalAlignment.Stretch;
            sheet.Height = double.NaN;
        }
        else
        {
            sheet.VerticalAlignment = VerticalAlignment.Bottom;
            sheet.Height = Math.Max(240, Root.ActualHeight * SettingsSheetModel.HeightFraction);
        }

        DispatcherQueue.TryEnqueue(() => _settingsSheet?.FocusSearch());
    }

    // Esc closes the sheet, or first clears the search text in it.
    private bool HandleSettingsEscape() => _viewModel.SettingsSheet.Dismiss();
}
