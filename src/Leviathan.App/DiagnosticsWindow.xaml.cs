using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Diagnostics.Crash;
using Leviathan.Engine.Supervision;
using Leviathan.ViewModels.Diagnostics;
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
    private readonly CrashList _crashes = new(new CrashBundleStore(new FileSystem(), CrashBundleStore.DefaultRoot));

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
        _ = RefreshCrashesAsync(select: null);
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

    // ---- Crashes (spec §20.4): grouped by signature, Copy for Claude, Open folder, Report a problem

    // Reading the bundles is file work, so it runs off the UI thread; the list keeps its selection by signature.
    private async Task RefreshCrashesAsync(string? select)
    {
        var keep = select ?? (CrashListView.SelectedItem as CrashRow)?.Signature;
        var rows = await Task.Run(() => _crashes.Rows());
        CrashListView.ItemsSource = rows;
        CrashEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CrashListView.SelectedItem = keep is null ? null : rows.FirstOrDefault(r => r.Signature == keep);
        UpdateCrashButtons();
    }

    private void UpdateCrashButtons()
    {
        var chosen = CrashListView.SelectedItem is CrashRow;
        CopyReportButton.IsEnabled = chosen;
        OpenBundleButton.IsEnabled = chosen;
    }

    private void OnCrashSelected(object sender, SelectionChangedEventArgs e) => UpdateCrashButtons();

    private async void OnRefreshCrashesClick(object sender, RoutedEventArgs e) => await RefreshCrashesAsync(select: null);

    private async void OnCopyReportClick(object sender, RoutedEventArgs e)
    {
        if (CrashListView.SelectedItem is not CrashRow row)
        {
            return;
        }

        var text = await Task.Run(() => _crashes.ReportFor(row.Signature));
        if (text is null)
        {
            FooterText.Text = "That crash report is gone. Refresh the list.";
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        FooterText.Text = "Copied the report. Paste it to Claude with the repository open.";
    }

    private void OnOpenBundleClick(object sender, RoutedEventArgs e)
    {
        if (CrashListView.SelectedItem is CrashRow row && _crashes.FolderFor(row.Signature) is { } folder)
        {
            OpenInExplorer(folder);
        }
    }

    private void OnOpenCrashesClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_crashes.Root);
        OpenInExplorer(_crashes.Root);
    }

    private void OpenInExplorer(string folder)
    {
        try
        {
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FooterText.Text = $"Could not open Explorer. The folder is {folder}";
        }
    }

    // A problem without a crash writes the same bundle on demand (spec §20.2), with the user's words in it.
    private async void OnReportProblemClick(object sender, RoutedEventArgs e)
    {
        var note = ReportNoteBox.Text.Trim();
        var folder = await Task.Run(() => App.Crashes.Report(IncidentKind.Report, null, note.Length == 0 ? null : note));
        if (folder is null)
        {
            FooterText.Text = "Could not write the report.";
            return;
        }

        ReportNoteBox.Text = string.Empty;
        await RefreshCrashesAsync(select: null);
        FooterText.Text = $"Saved a report to {folder}. Select it and choose Copy for Claude.";
    }

    private void ShowStalls() => FooterText.Text = $"UI stalls over 50 ms since start: {App.StallCount}";
}
