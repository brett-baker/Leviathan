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

        // The application's theme can only be chosen here, before anything is drawn; "same as Windows" leaves it alone.
        switch (ThemePeek.Read(Path.Combine(AppHost.DataFolder(), "settings.json")))
        {
            case ViewModels.Settings.ThemeMode.Light:
                RequestedTheme = ApplicationTheme.Light;
                break;
            case ViewModels.Settings.ThemeMode.Dark:
                RequestedTheme = ApplicationTheme.Dark;
                break;
        }

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

    /// <summary>The tab being dragged between windows, so a strip that receives the drop knows what it is (the drag stays inside this process).</summary>
    internal static ViewModels.Tabs.TabViewModel? DraggedTab { get; set; }

    /// <summary>The tab group being dragged by its chip, so a strip that receives the drop knows what it is.</summary>
    internal static ViewModels.Tabs.TabGroup? DraggedGroup { get; set; }

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
            Actions.Recorded += (_, action) => Host.Activity.Add(action);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Host.Activity.Dispose();
        }

        Host.Windows.WindowOpened += (_, window) => queue.TryEnqueue(() => Show(window));
        Host.Windows.WindowClosed += (_, window) => queue.TryEnqueue(() => CloseWindow(window));
        Host.Windows.AllWindowsClosed += (_, _) => queue.TryEnqueue(Shutdown);

        s_queue = queue;
        _ = Host.LoadBackgroundDataAsync(CancellationToken.None);
        _ = StartAsync();
        UiTrace.Mark("OnLaunched end");
    }

    /// <summary>
    /// Opens the first windows (spec §17.1): the settings, Last session and the profiles are read off the UI thread, then Last
    /// session or the chosen profile opens, or a fresh window when there is nothing to restore. Restored tabs list their folders
    /// only when shown, so a dead share in a background tab costs nothing at startup. Whatever goes wrong while reading, a window
    /// still opens.
    /// </summary>
    private async Task StartAsync()
    {
        ViewModels.Sessions.StartupPlan plan;
        using (UiTrace.Begin("PlanStartup"))
        {
            try
            {
                plan = await Task.Run(() => Host.PlanStartupAsync(CancellationToken.None));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
                UiTrace.Mark($"startup plan failed: {ex.Message}");
                plan = new ViewModels.Sessions.StartupPlan(ViewModels.Sessions.StartupAction.Fresh, null, null, "Your saved session could not be read; started fresh.");
            }
        }

        // The icon pack is a setting; it applies before any window draws its icons, and later changes redraw them.
        Host.ApplySettings(Host.Settings.Current);

        // Last session is only recorded once the first windows exist and, when the user is asked, once they have chosen, so a window
        // opened just to show the question never replaces the session they are about to pick. Subscribe before the window opens.
        if (plan.Action == ViewModels.Sessions.StartupAction.Ask)
        {
            _pendingPick = plan;
            Host.Windows.WindowOpened += OnFirstWindowForPicker;
        }

        using (UiTrace.Begin("Open first windows"))
        {
            var opened = plan.Action is ViewModels.Sessions.StartupAction.LastSession or ViewModels.Sessions.StartupAction.Profile && plan.State is { } state
                ? ViewModels.Sessions.SessionCapture.Restore(Host.Windows, state)
                : 0;
            if (opened == 0)
            {
                Host.Windows.OpenWindow();
            }
        }

        if (plan.Action != ViewModels.Sessions.StartupAction.Ask)
        {
            Host.Recorder.Start();
        }

        if (plan.Note is { } note)
        {
            // The window is shown from a queued callback; say it once that has happened.
            s_queue!.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _windows.Values.FirstOrDefault()?.ShowMessage(note));
        }
    }

    private ViewModels.Sessions.StartupPlan? _pendingPick;

    private void OnFirstWindowForPicker(object? sender, WindowViewModel viewModel)
    {
        Host.Windows.WindowOpened -= OnFirstWindowForPicker;
        s_queue!.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            if (_windows.TryGetValue(viewModel.Id, out var window) && _pendingPick is { } plan)
            {
                _pendingPick = null;
                await window.AskStartupAsync(plan);
                Host.Recorder.Start();
            }
        });
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
        UiTrace.Mark($"Show window; place explicitly: {viewModel.PlaceExplicitly}");
        if (viewModel.PlaceExplicitly)
        {
            MainWindow.PlaceWindow(window, viewModel);
            UiTrace.Mark("window placed");
        }

        using (UiTrace.Begin("MainWindow.Activate"))
        {
            window.Activate();
        }
    }

    private void CloseWindow(WindowViewModel viewModel)
    {
        UiTrace.Mark("App.CloseWindow requested");
        if (_windows.Remove(viewModel.Id, out var window))
        {
            window.Close();
        }
    }

    private void Shutdown()
    {
        // Closing the last window ends the UI process; a busy worker keeps running on its own (spec 4.2). The stall watchdog was
        // already stopped when that window began to close. The session was queued for writing when that window began to close; the
        // process must not end before the write does, so wait for it, briefly (a few kilobytes; the limit is for a stuck disk).
        Host.Recorder.Saved.Wait(TimeSpan.FromSeconds(2));
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

            // The last window is going: its tabs are still there now, so this is the session to keep (spec §17.1).
            _ = Host.Recorder.SaveFinalAsync();
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
