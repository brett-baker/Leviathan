using System.IO.Abstractions;
using Leviathan.Diagnostics;
using Leviathan.Diagnostics.Crash;
using Microsoft.UI.Xaml;

namespace Leviathan.App;

/// <summary>Application entry: crash handling first, then the main window.</summary>
public partial class App : Application
{
    private const string ProcessName = "Leviathan.App";

    private MainWindow? _window;

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

    /// <summary>The UI process's flight recorder.</summary>
    public static FlightRecorder Recorder { get; private set; } = null!;

    /// <summary>The user's recent commands.</summary>
    public static ActionTrail Actions { get; private set; } = null!;

    /// <summary>Crash bundle writer.</summary>
    public static CrashHandlers Crashes { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
