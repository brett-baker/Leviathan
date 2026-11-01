using System.Collections.ObjectModel;
using Leviathan.Contracts;
using Leviathan.Engine.Supervision;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>
/// The Phase 0 worker demo, kept as a diagnostics window (Options, Diagnostics): worker connection and restarts, the job
/// queue, folder sizes and the UI stall count. It starts its own supervised worker connection when opened.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The worker connection is disposed asynchronously from the Closed event, which a Window cannot express as IDisposable.")]
public sealed partial class DiagnosticsWindow : Window
{
    private readonly WorkerConnection _worker;

    /// <summary>Creates the window and starts the worker connection.</summary>
    public DiagnosticsWindow()
    {
        InitializeComponent();
        Title = "Leviathan diagnostics";

        _worker = new WorkerConnection(new DispatcherQueueUiDispatcher(DispatcherQueue), Jobs);
        _worker.StatusChanged += (_, status) => ShowStatus(status);

        void OnStall(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ShowStalls);
        App.StallsChanged += OnStall;
        ShowStalls();

        Closed += async (_, _) =>
        {
            App.StallsChanged -= OnStall;
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

    private void ShowStalls() => FooterText.Text = $"UI stalls over 50 ms since start: {App.StallCount}";
}
