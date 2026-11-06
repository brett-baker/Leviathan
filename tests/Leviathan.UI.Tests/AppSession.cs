using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Leviathan.UI.Tests;

/// <summary>
/// One run of Leviathan for one test (spec §19, build plan H1): started with its own data folder so it never touches the user's
/// settings or session, driven through UI Automation, closed afterwards. The UI stall watchdog is read from the status bar: a test
/// fails when the UI stalled for more than 50 ms while the test was acting (spec §3.2).
/// </summary>
internal sealed class AppSession : IAsyncDisposable
{
    private readonly Application _app;
    private readonly UIA3Automation _automation;
    private int _baselineStalls;

    private AppSession(Application app, UIA3Automation automation, Window window, string dataFolder)
    {
        _app = app;
        _automation = automation;
        Window = window;
        DataFolder = dataFolder;
    }

    /// <summary>False when the app has exited (a crash, or a test that closed it).</summary>
    public bool IsRunning => !_app.HasExited;

    /// <summary>The main window.</summary>
    public Window Window { get; }

    /// <summary>The UI trace of this run (stalls with their length, and what the UI was doing).</summary>
    public string TracePath => Path.Combine(DataFolder, "ui-trace.txt");

    /// <summary>The folder the app keeps its settings in for this run.</summary>
    public string DataFolder { get; }

    /// <summary>Where Leviathan.exe is: the <c>LEVIATHAN_APP_EXE</c> environment variable, else the build output next to the sources.</summary>
    public static string ExePath()
    {
        if (Environment.GetEnvironmentVariable("LEVIATHAN_APP_EXE") is { Length: > 0 } given)
        {
            return given;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Leviathan.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("The solution folder was not found above the test.");
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(root, "src", "Leviathan.App", "bin", "x64", configuration, "net10.0-windows10.0.26100.0", "Leviathan.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Leviathan.exe was not found. Build the solution first, or set LEVIATHAN_APP_EXE.");
    }

    /// <summary>Starts the app and waits for its window and for the first folder.</summary>
    /// <param name="settingsJson">Text for <c>settings.json</c>, or null for the defaults. <c>General.NewTabLocation</c> decides the folder the first tab opens in.</param>
    public static async Task<AppSession> StartAsync(string? settingsJson = null)
    {
        // These tests take over the mouse and the keyboard of an interactive desktop and need it to themselves: they are run on purpose.
        Assert.SkipUnless(Environment.GetEnvironmentVariable("LEVIATHAN_UI_TESTS") == "1", "The UI tests drive the real app with the real mouse and keyboard. Set LEVIATHAN_UI_TESTS=1 and leave the desktop alone while they run.");

        var data = Path.Combine(Path.GetTempPath(), "leviathan-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        if (settingsJson is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), settingsJson);
        }

        var info = new ProcessStartInfo(ExePath()) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath())! };
        // The data folder also names the worker (pipe and mutex), so this run starts a worker of its own and never uses the user's.
        info.Environment["LEVIATHAN_DATA_DIR"] = data;
        info.Environment["LEVIATHAN_UI_TRACE"] = Path.Combine(data, "ui-trace.txt");

        var app = Application.Launch(info);
        var automation = new UIA3Automation();
        Window? window = null;
        Retry.WhileNull(() => window = app.GetMainWindow(automation, TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250));
        if (window is null)
        {
            app.Kill();
            throw new TimeoutException("Leviathan did not open a window within a minute.");
        }

        var started = new AppSession(app, automation, window, data);
        await started.SettleAsync();
        return started;
    }

    /// <summary>Waits until the first folder is listed, then lets startup work end, and takes the stall count as the baseline.</summary>
    private async Task SettleAsync()
    {
        Window.Focus();
        Wait.Until(() => Find("FileList") is not null, TimeSpan.FromSeconds(30), "the file list");
        await Task.Delay(TimeSpan.FromSeconds(3));
        _baselineStalls = Stalls();
    }

    private AutomationElement? _stallText;

    /// <summary>The number of UI stalls the app reports in its status bar (read from an element found once, so reading it costs the app almost nothing).</summary>
    public int Stalls()
    {
        _stallText ??= Find("StallText");
        var text = _stallText?.Name ?? string.Empty;
        var digits = new string([.. text.Where(char.IsDigit)]);
        return int.TryParse(digits, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    /// <summary>
    /// A window in which the test only acts: it fails when the UI stalled for more than 50 ms inside it (spec 3.2). Searching the
    /// automation tree is not acting: a client walking a WinUI tree makes the app stall by itself (300 to 500 ms for a walk), so
    /// elements are found before the window opens and results are checked after it closes.
    /// </summary>
    public IDisposable Acting(string label = "", int budgetMs = 50) => new ActingWindow(this, label, budgetMs);

    private sealed class ActingWindow : IDisposable
    {
        private readonly AppSession _session;
        private readonly int _before;
        private readonly long _traceFrom;

        private readonly string _label;

        private readonly int _budgetMs;

        public ActingWindow(AppSession session, string label, int budgetMs)
        {
            _session = session;
            _label = label;
            _budgetMs = budgetMs;
            _before = session.Stalls();
            _traceFrom = File.Exists(session.TracePath) ? new FileInfo(session.TracePath).Length : 0;
        }

        public void Dispose()
        {
            Thread.Sleep(600);
            if (_session.Stalls() == _before)
            {
                return;
            }

            // Something stalled; the trace says for how long (it is written a little after the event). More than the budget fails.
            Wait.TryUntil(() => Longest() > 0, TimeSpan.FromSeconds(4));
            var longest = Longest();
            if (longest > _budgetMs)
            {
                Assert.Fail($"The UI stalled for {longest} ms (the limit is {_budgetMs} ms) while the test acted ({_label}): {Describe()}");
            }
        }

        private int Longest()
        {
            var longest = 0;
            foreach (var line in Describe().Split(';'))
            {
                var at = line.IndexOf("STALL ", StringComparison.Ordinal);
                if (at >= 0 && int.TryParse(new string([.. line[(at + 6)..].TakeWhile(char.IsDigit)]), CultureInfo.InvariantCulture, out var ms))
                {
                    longest = Math.Max(longest, ms);
                }
            }

            return longest;
        }

        private string Describe()
        {
            try
            {
                using var stream = new FileStream(_session.TracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(_traceFrom, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var lines = reader.ReadToEnd().Split((char)10).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                var stalls = lines.Where(l => l.Contains("STALL", StringComparison.Ordinal)).ToList();

                // The trace is written a moment after the event: the lines around the stalls say what the UI was doing.
                return string.Join(" ; ", stalls.Count > 0 ? stalls : lines.TakeLast(6));
            }
            catch (IOException)
            {
                return "(the trace could not be read)";
            }
        }
    }

    /// <summary>Finds an element by its x:Name (AutomationId) or its name, anywhere in the main window.</summary>
    public AutomationElement? Find(string idOrName) =>
        Window.FindFirstDescendant(cf => cf.ByAutomationId(idOrName)) ?? Window.FindFirstDescendant(cf => cf.ByName(idOrName));

    /// <summary>Finds an element or fails after <paramref name="seconds"/> with a message naming it.</summary>
    public AutomationElement Get(string idOrName, int seconds = 10)
    {
        AutomationElement? found = null;
        Wait.Until(() => (found = Find(idOrName)) is not null, TimeSpan.FromSeconds(seconds), idOrName);
        return found!;
    }

    /// <summary>Saves a picture of the window to the temp folder (<c>leviathan-ui-&lt;name&gt;.png</c>), for looking at a failure.</summary>
    public void Screenshot(string name)
    {
        try
        {
            Window.CaptureToFile(Path.Combine(Path.GetTempPath(), $"leviathan-ui-{name}.png"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A picture is a help, never a reason for a test to fail differently.
        }
    }

    /// <summary>
    /// True when the element with this name is a selected list item. A destructive key is sent only after this says the thing about to be
    /// deleted is the thing the test made (the tests drive the real mouse and keyboard and the user's own worker).
    /// </summary>
    public bool IsSelected(string name) =>
        Find(name)?.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault == true;

    /// <summary>True when an element with this id or name is on screen.</summary>
    public bool Has(string idOrName) => Find(idOrName) is { IsOffscreen: false };

    /// <summary>Where to click an element, found before the test starts acting (so no tree search disturbs the app while it is measured).</summary>
    public Point PointOf(string idOrName) => Get(idOrName).GetClickablePoint();

    /// <summary>How many windows of the app are open.</summary>
    public int WindowCount() => _app.GetAllTopLevelWindows(_automation).Count(w => w.Properties.ClassName.ValueOrDefault?.StartsWith("WinUIDesktop", StringComparison.Ordinal) == true);

    /// <summary>How many tabs (and group chips) the strip shows.</summary>
    public int TabCount() => Window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).Length;

    /// <summary>Clicks a point with the real mouse.</summary>
    public static void Tap(Point where)
    {
        Mouse.Click(where);
        Thread.Sleep(300);
    }

    /// <summary>Clicks a point with the right mouse button.</summary>
    public static void TapRight(Point where)
    {
        Mouse.RightClick(where);
        Thread.Sleep(400);
    }

    /// <summary>Presses a key chord with real keyboard input (the window must be the foreground one, which it is after a click in it).</summary>
    public static void Press(params VirtualKeyShort[] keys)
    {
        if (keys.Length == 1)
        {
            Keyboard.Type(keys[0]);
        }
        else
        {
            Keyboard.TypeSimultaneously(keys);
        }

        Thread.Sleep(300);
    }

    /// <summary>Types text with real keyboard input.</summary>
    public static void TypeText(string text)
    {
        Keyboard.Type(text);
        Thread.Sleep(300);
    }

    /// <summary>Clicks an empty part of the file list, so the keyboard belongs to the window and nothing is selected.</summary>
    public void FocusFileList()
    {
        var list = Get("FileList").BoundingRectangle;
        Tap(new Point(list.Right - 40, list.Bottom - 40));
    }

    // The worker and the shell host are separate processes that outlive the window; the ones of this build are stopped too.
    private static void StopHelpers()
    {
        var folder = Path.GetDirectoryName(ExePath())!;
        foreach (var name in new[] { "Leviathan.Worker", "Leviathan.ShellHost" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.MainModule?.FileName is { } path && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Gone already, or not ours to stop.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            if (!_app.HasExited)
            {
                _app.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        StopHelpers();
        _automation.Dispose();
        _app.Dispose();
        try
        {
            Directory.Delete(DataFolder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the worker may still hold a file for a moment.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Waits for a condition with a timeout, and says what it was waiting for when it gives up.</summary>
internal static class Wait
{
    public static bool TryUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    public static void Until(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(100);
        }
    }
}
