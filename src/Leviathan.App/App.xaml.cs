using System.IO.Abstractions;
using Leviathan.Diagnostics;
using Leviathan.Diagnostics.Crash;
using Leviathan.ViewModels.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Leviathan.App;

/// <summary>Application entry: crash handling first, then the windows.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The watchdog lives for the whole process and is disposed when the last window closes; the Application class cannot be IDisposable.")]
public partial class App : Application
{
    private const string ProcessName = "Leviathan.App";

    private readonly Dictionary<Guid, MainWindow> _windows = [];
    private UiStallWatchdog? _watchdog;

    /// <summary>Creates the application.</summary>
    public App()
    {
        Recorder = new FlightRecorder(ProcessName);
        Actions = new ActionTrail();
        var builder = new CrashReportBuilder(ProcessName, CrashReportBuilder.ReadBuildInfo(typeof(App).Assembly), Recorder, Actions);
        Crashes = new CrashHandlers(builder, new CrashBundleStore(new FileSystem(), CrashBundleStore.DefaultRoot));
        Crashes.Install();

        InitializeComponent();

        // WinUI-specific: exceptions on the UI thread. Write the bundle, then let the app crash as normal.
        UnhandledException += (_, e) => Crashes.Report(IncidentKind.Crash, e.Exception);
    }

    /// <summary>Raised (on any thread) when the stall watchdog counts another UI stall.</summary>
    internal static event EventHandler? StallsChanged;

    /// <summary>The UI process's flight recorder.</summary>
    public static FlightRecorder Recorder { get; private set; } = null!;

    /// <summary>The user's recent commands.</summary>
    public static ActionTrail Actions { get; private set; } = null!;

    /// <summary>Crash bundle writer.</summary>
    public static CrashHandlers Crashes { get; private set; } = null!;

    /// <summary>The services all windows share.</summary>
    internal static AppHost Host { get; private set; } = null!;

    /// <summary>UI stalls over the threshold (50 ms) since start (spec §3.2).</summary>
    internal static int StallCount => Volatile.Read(ref _stallCount);

    private static int _stallCount;

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        Host = AppHost.Create(new DispatcherQueueUiContext(queue));
        Host.Windows.WindowOpened += (_, window) => queue.TryEnqueue(() => Show(window));
        Host.Windows.WindowClosed += (_, window) => queue.TryEnqueue(() => CloseWindow(window));
        Host.Windows.AllWindowsClosed += (_, _) => queue.TryEnqueue(Shutdown);

        StartWatchdog(queue);
        Host.Windows.OpenWindow();
    }

    private void Show(WindowViewModel viewModel)
    {
        var window = new MainWindow(viewModel);
        _windows[viewModel.Id] = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(viewModel.Id);
            Host.Windows.Close(viewModel);
        };
        window.Activate();
    }

    private void CloseWindow(WindowViewModel viewModel)
    {
        if (_windows.Remove(viewModel.Id, out var window))
        {
            window.Close();
        }
    }

    private void Shutdown()
    {
        // Closing the last window ends the UI process; a busy worker keeps running on its own (spec 4.2).
        _ = _watchdog?.DisposeAsync().AsTask();
        Exit();
    }

    /// <summary>The watchdog runs in every UI build: a stall over 50 ms is a bug, a hang over 5 s writes a crash bundle (spec §3.2, §20.2).</summary>
    private void StartWatchdog(DispatcherQueue queue)
    {
        _watchdog = new UiStallWatchdog(new DispatcherQueueUiDispatcher(queue));
        _watchdog.StallDetected += (_, stall) =>
        {
            Interlocked.Increment(ref _stallCount);
            Recorder.Record(Microsoft.Extensions.Logging.LogLevel.Warning, "UiStallWatchdog", $"UI thread stalled for {stall.Duration.TotalMilliseconds:0} ms{(stall.IsHang ? " (hang)" : string.Empty)}");
            if (stall.IsHang)
            {
                Crashes.Report(IncidentKind.Hang, null, $"UI thread unresponsive for {stall.Duration.TotalSeconds:0.0} s");
            }

            StallsChanged?.Invoke(this, EventArgs.Empty);
        };
        _watchdog.Start();
    }
}
