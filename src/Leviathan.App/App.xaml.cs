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
    private static DispatcherQueue? s_queue;
    private static UiStallWatchdog? s_watchdog;
    private static int s_ready;
    private static int s_watching;

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
    internal static int StallCount => Volatile.Read(ref s_stallCount);

    private static int s_stallCount;

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiTrace.Mark("OnLaunched start");
        var queue = DispatcherQueue.GetForCurrentThread();
        using (UiTrace.Begin("AppHost.Create"))
        {
            Host = AppHost.Create(new DispatcherQueueUiContext(queue));
        }

        Host.Windows.WindowOpened += (_, window) => queue.TryEnqueue(() => Show(window));
        Host.Windows.WindowClosed += (_, window) => queue.TryEnqueue(() => CloseWindow(window));
        Host.Windows.AllWindowsClosed += (_, _) => queue.TryEnqueue(Shutdown);

        s_queue = queue;
        using (UiTrace.Begin("Windows.OpenWindow (incl. first tab)"))
        {
            Host.Windows.OpenWindow();
        }

        UiTrace.Mark("OnLaunched end");
    }

    private void Show(WindowViewModel viewModel)
    {
        MainWindow window;
        using (UiTrace.Begin("new MainWindow"))
        {
            window = new MainWindow(viewModel);
        }

        _windows[viewModel.Id] = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(viewModel.Id);
            Host.Windows.Close(viewModel);
        };
        using (UiTrace.Begin("MainWindow.Activate"))
        {
            window.Activate();
        }
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
        // Closing the last window ends the UI process; a busy worker keeps running on its own (spec 4.2). The stall watchdog was
        // already stopped when that window began to close.
        Exit();
    }

    /// <summary>
    /// Called when a part of startup has finished. The stall watchdog starts only once the first folder is listed and the sidebar
    /// is filled and the UI is idle: building the first window and laying out its first frame is cold start, which has its own
    /// budget (spec 3.1, under 600 ms), not a stall of a window someone is using.
    /// </summary>
    internal static void ReportReady(ReadyPart part)
    {
        var ready = Interlocked.Or(ref s_ready, (int)part) | (int)part;
        if (ready == (int)ReadyPart.All && Interlocked.Exchange(ref s_watching, 1) == 0 && s_queue is { } queue)
        {
            var coldStart = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            Recorder.Record(Microsoft.Extensions.Logging.LogLevel.Information, "Startup", $"Cold start: first folder and sidebar ready {coldStart.TotalMilliseconds:0} ms after the process started");
            UiTrace.Mark($"READY: cold start {coldStart.TotalMilliseconds:0} ms; stall watchdog starts when the UI is idle");
            queue.TryEnqueue(DispatcherQueuePriority.Low, BeginWatching);
        }
    }

    /// <summary>
    /// Stops the watchdog when the last window starts to close: tearing the window down takes a moment of UI time that is not a
    /// freeze of anything the user can see.
    /// </summary>
    internal static void NotifyWindowClosing(WindowViewModel window)
    {
        if (Host.Windows.Windows.Count <= 1 && Host.Windows.Windows.Contains(window))
        {
            _ = s_watchdog?.DisposeAsync().AsTask();
        }
    }

    /// <summary>The watchdog runs in every UI build: a stall over 50 ms is a bug, a hang over 5 s writes a crash bundle (spec 3.2, 20.2).</summary>
    private static void BeginWatching()
    {
        s_watchdog = new UiStallWatchdog(new DispatcherQueueUiDispatcher(s_queue!));
        s_watchdog.StallDetected += (_, stall) =>
        {
            Interlocked.Increment(ref s_stallCount);
            UiTrace.Mark($"*** STALL {stall.Duration.TotalMilliseconds:F0} ms (watchdog saw a heartbeat delayed this long)");
            Recorder.Record(Microsoft.Extensions.Logging.LogLevel.Warning, "UiStallWatchdog", $"UI thread stalled for {stall.Duration.TotalMilliseconds:0} ms{(stall.IsHang ? " (hang)" : string.Empty)}");
            if (stall.IsHang)
            {
                Crashes.Report(IncidentKind.Hang, null, $"UI thread unresponsive for {stall.Duration.TotalSeconds:0.0} s");
            }

            StallsChanged?.Invoke(null, EventArgs.Empty);
        };
        s_watchdog.Start();
    }
}

/// <summary>The parts of startup that must be done before the stall watchdog starts.</summary>
[Flags]
internal enum ReadyPart
{
    /// <summary>The first tab's first listing has finished (success or failure).</summary>
    FirstListing = 1,

    /// <summary>The sidebar has been filled.</summary>
    Sidebar = 2,

    /// <summary>Everything.</summary>
    All = FirstListing | Sidebar,
}
