using Microsoft.Extensions.Logging;

namespace Leviathan.Diagnostics.Crash;

/// <summary>
/// Installs process-wide handlers that write a crash bundle for unhandled and unobserved exceptions (spec §20.2).
/// The UI adds its own WinUI handler and calls <see cref="Report"/> from it.
/// </summary>
public sealed class CrashHandlers : IDisposable
{
    private readonly CrashReportBuilder _builder;
    private readonly CrashBundleStore _store;
    private readonly Func<IReadOnlyDictionary<string, string>>? _context;
    private readonly ILogger? _logger;
    private int _installed;

    /// <summary>Creates the handlers. Call <see cref="Install"/> to attach them.</summary>
    public CrashHandlers(CrashReportBuilder builder, CrashBundleStore store, Func<IReadOnlyDictionary<string, string>>? context = null, ILogger? logger = null)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _context = context;
        _logger = logger;
    }

    /// <summary>Raised after a bundle is written, with its folder.</summary>
    public event EventHandler<string>? BundleWritten;

    /// <summary>Attaches to <see cref="AppDomain.UnhandledException"/> and <see cref="TaskScheduler.UnobservedTaskException"/>.</summary>
    public void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    /// <summary>Writes a bundle for <paramref name="exception"/> and returns its folder (null if writing failed).</summary>
    public string? Report(IncidentKind kind, Exception? exception, string? userNote = null)
    {
        IReadOnlyDictionary<string, string>? context = null;
        try
        {
            context = _context?.Invoke();
        }
        catch (Exception ex)
        {
            // Gathering context must never prevent the report.
            context = new Dictionary<string, string> { ["contextError"] = ex.Message };
        }

        var report = _builder.Build(kind, exception, context, userNote);
        var folder = _store.TryWrite(report);
        if (folder is not null)
        {
            _logger?.LogCritical("{Kind} report written to {Folder}: {Summary}", kind, folder, report.Summary);
            BundleWritten?.Invoke(this, folder);
        }

        return folder;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 1)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private void OnUnhandled(object sender, UnhandledExceptionEventArgs e) =>
        Report(IncidentKind.Crash, e.ExceptionObject as Exception ?? new InvalidOperationException($"Non-exception thrown: {e.ExceptionObject}"));

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Record it, but do not crash the process for an unobserved task.
        Report(IncidentKind.Crash, e.Exception);
        e.SetObserved();
    }
}
