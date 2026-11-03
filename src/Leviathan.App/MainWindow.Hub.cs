using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Hub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Leviathan.App;

/// <summary>A window of its own for the status hub when it is popped out (spec §16.1).</summary>
internal sealed class HubWindow : Window
{
    public HubWindow(StatusHubPanel panel, Action<HubWindow> closed)
    {
        Title = "Leviathan: status";
        Content = panel;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 520));
        Closed += (_, _) =>
        {
            Content = null;
            closed(this);
        };
    }
}

/// <summary>
/// The status hub in the window (spec §16.1, D34): the chevron, the operations chip, Ctrl+Shift+O and the status-bar parts open it;
/// it flies up over the bottom of the file view, or is docked under or beside it, or lives in its own window. This file places the
/// panel and wires the entry points; the rules (what Esc does, the limits of the size) are in <see cref="StatusHubModel"/>.
/// </summary>
public sealed partial class MainWindow
{
    private StatusHubPanel? _hubPanel;
    private HubWindow? _hubWindow;
    private int _operationsDirty;
    private int _activityDirty;

    private void InitHub()
    {
        var hub = _viewModel.Hub;
        hub.Apply(App.Host.HubLayout);
        hub.LayoutChanged += (_, layout) => App.Host.SaveHubLayout(layout);
        var panel = new StatusHubPanel(hub, App.Host.Operations, App.Host.Activity, App.Host.Background) { CanUndo = () => App.Host.Undo.CanUndo };
        _hubPanel = panel;
        BodyArea.Children.Add(panel);
        panel.HeightRequested += (_, height) => hub.SetHeight(height, BodyArea.ActualHeight);
        panel.WidthRequested += (_, width) => hub.SetWidth(width, BodyArea.ActualWidth);
        panel.PlaceRequested += (_, placement) => hub.Place(placement);
        panel.GoToRequested += (_, where) => Run(CommandIds.NavGo, where);
        panel.UndoRequested += (_, _) => Run(CommandIds.EditUndo);
        panel.ExportRequested += async (_, export) => await ExportActivityAsync(export.Text, export.Extension);
        App.Host.Activity.Changed += OnActivityChanged;
        App.Host.Background.Changed += OnBackgroundChanged;
        hub.PropertyChanged += (_, _) => OnHubChanged();
        BodyArea.SizeChanged += (_, _) => ApplyHubPlacement();
        App.Host.Operations.Changed += OnOperationsChanged;
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressedForHub), true);
        Icons.Bind(HubChevron, static (b, i) => b.Content = i, "chevron-up", 12);
        ApplyHubPlacement();
        UpdateOperationsPart();
    }

    private void OnHubChevronClick(object sender, RoutedEventArgs e) => Run(CommandIds.HubToggle);

    private void OnHubOperationsClick(object sender, RoutedEventArgs e) => Run(CommandIds.HubToggle, parameter: HubTab.Operations);

    private void OnFreeSpaceTapped(object sender, TappedRoutedEventArgs e) => Run(CommandIds.HubToggle, parameter: HubTab.Space);

    private void OnHubChanged()
    {
        ApplyHubPlacement();
        var hub = _viewModel.Hub;
        if (hub.IsOpen && hub.Tab == HubTab.Operations && App.Host.Operations.UnseenFailures > 0)
        {
            App.Host.Operations.MarkFailuresSeen();
        }

        ToolTipService.SetToolTip(HubChevron, hub.IsOpen ? "Close the status hub (Esc, Ctrl+Shift+O)" : "Operations, activity, background tasks and quick settings (Ctrl+Shift+O)");
        UpdateOperationsPart();
    }

    // Puts the panel where the model says: over the bottom of the file view, under it, beside it, or in its own window.
    private void ApplyHubPlacement()
    {
        if (_hubPanel is not { } panel || _closed)
        {
            return;
        }

        var hub = _viewModel.Hub;
        if (hub.Placement == HubPlacement.PoppedOut && hub.IsOpen)
        {
            OpenHubWindow(panel);
            return;
        }

        if (_hubWindow is not null)
        {
            CloseHubWindow();
        }

        panel.ShowGripFor(hub.Placement);
        panel.Visibility = hub.IsOpen && hub.Placement != HubPlacement.PoppedOut ? Visibility.Visible : Visibility.Collapsed;
        switch (hub.Placement)
        {
            case HubPlacement.DockRight:
                Grid.SetRow(panel, 0);
                Grid.SetRowSpan(panel, 2);
                Grid.SetColumn(panel, 1);
                Grid.SetColumnSpan(panel, 1);
                panel.VerticalAlignment = VerticalAlignment.Stretch;
                panel.HorizontalAlignment = HorizontalAlignment.Right;
                panel.Height = double.NaN;
                panel.Width = hub.Width;
                break;
            case HubPlacement.DockBottom:
                Grid.SetRow(panel, 1);
                Grid.SetRowSpan(panel, 1);
                Grid.SetColumn(panel, 0);
                Grid.SetColumnSpan(panel, 2);
                panel.VerticalAlignment = VerticalAlignment.Stretch;
                panel.HorizontalAlignment = HorizontalAlignment.Stretch;
                panel.Width = double.NaN;
                panel.Height = HeightFor(hub);
                break;
            default:
                Grid.SetRow(panel, 0);
                Grid.SetRowSpan(panel, 2);
                Grid.SetColumn(panel, 0);
                Grid.SetColumnSpan(panel, 2);
                panel.VerticalAlignment = VerticalAlignment.Bottom;
                panel.HorizontalAlignment = HorizontalAlignment.Stretch;
                panel.Width = double.NaN;
                panel.Height = HeightFor(hub);
                break;
        }
    }

    private double HeightFor(StatusHubModel hub) => Math.Max(120, BodyArea.ActualHeight * hub.HeightFraction);

    private void OpenHubWindow(StatusHubPanel panel)
    {
        if (_hubWindow is not null)
        {
            return;
        }

        BodyArea.Children.Remove(panel);
        panel.Width = double.NaN;
        panel.Height = double.NaN;
        panel.ShowGripFor(HubPlacement.PoppedOut);
        panel.Visibility = Visibility.Visible;
        _hubWindow = new HubWindow(panel, OnHubWindowClosed);
        _hubWindow.Activate();
    }

    private void CloseHubWindow()
    {
        var window = _hubWindow;
        _hubWindow = null;
        window?.Close();
        if (_hubPanel is { } panel && !BodyArea.Children.Contains(panel))
        {
            BodyArea.Children.Add(panel);
        }
    }

    // The user closed the pop-out window: the hub goes back to being a closed fly-up.
    private void OnHubWindowClosed(HubWindow window)
    {
        if (!ReferenceEquals(window, _hubWindow))
        {
            return;
        }

        _hubWindow = null;
        if (_hubPanel is { } panel && !BodyArea.Children.Contains(panel))
        {
            BodyArea.Children.Add(panel);
        }

        if (!_closed)
        {
            _viewModel.Hub.Placement = HubPlacement.Flyout;
            _viewModel.Hub.Close();
        }
    }

    // A click anywhere outside an unpinned fly-up closes it (the status bar excepted: its parts open and close the hub themselves).
    private void OnPointerPressedForHub(object sender, PointerRoutedEventArgs e)
    {
        var hub = _viewModel.Hub;
        if (_hubPanel is not { } panel || !hub.IsOpen || hub.IsPinned || hub.Placement != HubPlacement.Flyout)
        {
            return;
        }

        if (Inside(panel, e) || Inside(StatusBar, e))
        {
            return;
        }

        hub.Dismiss();
    }

    private static bool Inside(FrameworkElement element, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(element).Position;
        return point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
    }

    // The log grows on whatever thread ran a command; one pass on the UI thread covers a burst.
    private void OnActivityChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _activityDirty, 1) == 0)
        {
            OnUi(() =>
            {
                Interlocked.Exchange(ref _activityDirty, 0);
                _hubPanel?.RefreshActivity();
            });
        }
    }

    private async Task ExportActivityAsync(string text, string extension)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "leviathan-activity" };
            picker.FileTypeChoices.Add(extension == "json" ? "JSON" : "Text", [$".{extension}"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSaveFileAsync() is { } file)
            {
                await File.WriteAllTextAsync(file.Path, text);
                MessageText.Text = $"Saved the activity log to {file.Name}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageText.Text = $"Could not save the log: {ex.Message}";
        }
    }

    private void OnBackgroundChanged(object? sender, EventArgs e) => OnUi(() => _hubPanel?.RefreshBackground());

    private void OnOperationsChanged(object? sender, EventArgs e)
    {
        // The worker reports from its own threads, often many times a second: one pass on the UI thread covers them all.
        if (Interlocked.Exchange(ref _operationsDirty, 1) == 0)
        {
            OnUi(() =>
            {
                Interlocked.Exchange(ref _operationsDirty, 0);
                UpdateOperationsPart();
                _hubPanel?.RefreshOperations();
                var hub = _viewModel.Hub;
                if (hub.IsOpen && hub.Tab == HubTab.Operations && App.Host.Operations.UnseenFailures > 0)
                {
                    App.Host.Operations.MarkFailuresSeen();
                }
            });
        }
    }

    // The chip in the status bar ("2 jobs · 1 queued · 112 MB/s · 2 min left") and the badge for failures nobody has seen.
    private void UpdateOperationsPart()
    {
        var operations = App.Host.Operations;
        var summary = operations.Summary;
        OperationsPart.Visibility = summary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        OperationsText.Text = summary;
        var failures = operations.UnseenFailures;
        HubBadge.Value = failures;
        HubBadge.Visibility = failures > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool HandleHubEscape()
    {
        var hub = _viewModel.Hub;
        if (hub.IsOpen && !IsEditingText() && hub.Dismiss())
        {
            return true;
        }

        return false;
    }
}
