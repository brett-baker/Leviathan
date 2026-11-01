using System.Collections.ObjectModel;
using Leviathan.Contracts;
using Leviathan.Diagnostics;
using Leviathan.Engine.Supervision;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>The Phase 0 shell window.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The worker connection and watchdog are disposed asynchronously from the Closed event, which a Window cannot express as IDisposable.")]
public sealed partial class MainWindow : Window
{
    private readonly WorkerConnection _worker;
    private readonly UiStallWatchdog _watchdog;

    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Title = "Leviathan";

        _worker = new WorkerConnection(new DispatcherQueueUiDispatcher(DispatcherQueue), Jobs);
        _worker.StatusChanged += (_, status) => ShowStatus(status);

        _watchdog = new UiStallWatchdog(new DispatcherQueueUiDispatcher(DispatcherQueue));
        _watchdog.StallDetected += OnStall;
        _watchdog.Start();

        Closed += async (_, _) =>
        {
            await _watchdog.DisposeAsync();
            await _worker.DisposeAsync();
        };

        _ = _worker.StartAsync();
    }

    /// <summary>Jobs shown in the list.</summary>
    public ObservableCollection<JobRow> Jobs { get; } = [];

    private async void OnComputeClick(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text;
        App.Actions.Record("ComputeFolderSize", path);
        if (!Core.Paths.LocationPath.TryParse(path, out var location, out var error))
        {
            FooterText.Text = error;
            return;
        }

        try
        {
            await _worker.SubmitAsync(new JobRequest(JobKind.ComputeFolderSize, [location.Value], null));
            FooterText.Text = $"Queued: {location}";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Could not queue the job: {ex.Message}";
        }
    }

    private void ShowStatus(WorkerStatus status)
    {
        StatusText.Text = status.Message;
        StatusDot.Fill = new SolidColorBrush(status.State switch
        {
            SupervisedState.Running when status.Connected => Colors.SeaGreen,
            SupervisedState.Disabled => Colors.Firebrick,
            _ => Colors.DarkOrange,
        });
    }

    private void OnStall(object? sender, UiStall stall)
    {
        App.Recorder.Record(Microsoft.Extensions.Logging.LogLevel.Warning, "UiStallWatchdog", $"UI thread stalled for {stall.Duration.TotalMilliseconds:0} ms{(stall.IsHang ? " (hang)" : string.Empty)}");
        if (stall.IsHang)
        {
            App.Crashes.Report(Diagnostics.Crash.IncidentKind.Hang, null, $"UI thread unresponsive for {stall.Duration.TotalSeconds:0.0} s");
        }

        DispatcherQueue.TryEnqueue(() => FooterText.Text = $"UI stalls detected: {_watchdog.StallCount}");
    }
}
