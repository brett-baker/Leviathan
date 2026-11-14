using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Providers.Local;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Views;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

/// <summary>
/// Idle memory of the real app with ten tabs (spec 3.1: under 250 MB): writes a "Last session" of ten tabs, each on its own folder of
/// 2,000 files, into a scratch data folder, starts the published Leviathan.exe on it, waits until it is ready and has been idle for
/// ten seconds, and reads the working set and private bytes of its processes. Nothing outside the scratch folders is touched.
/// Exit code 0 = the UI process is under the budget.
/// </summary>
internal static class IdleMemoryGate
{
    private static int Tabs { get; set; } = 10;
    private const double BudgetMb = 250;

    public static async Task<int> RunAsync(string exe, int tabs = 10)
    {
        Tabs = tabs;
        var data = Path.Combine(Path.GetTempPath(), "leviathan-idlemem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var folders = Enumerable.Range(0, Tabs).Select(i => Fixture.CreateFolder(2_000 + i)).ToList();
        try
        {
            await WriteSessionAsync(data, folders);
            var trace = Path.Combine(data, "ui-trace.txt");
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            info.Environment["LEVIATHAN_DATA_DIR"] = data;
            info.Environment["LEVIATHAN_UI_TRACE"] = trace;
            using var process = Process.Start(info)!;
            if (ColdStartGate.WaitForReady(trace, TimeSpan.FromSeconds(30)) is null)
            {
                Console.Error.WriteLine("The app did not report READY within 30 s.");
                return 1;
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
            process.Refresh();
            var ui = Describe(process);
            var helpers = new List<string>();
            foreach (var name in new[] { "Leviathan.Worker", "Leviathan.ShellHost", "Leviathan.Media" })
            {
                foreach (var other in Process.GetProcessesByName(name).Where(p => ColdStartGate.IsUnder(p, Path.GetDirectoryName(exe)!)))
                {
                    helpers.Add($"{name} {Describe(other)}");
                    other.Kill();
                }
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Gone already.
            }

            var pass = process.WorkingSet64 / 1048576.0 <= BudgetMb || ui.Contains("PASS", StringComparison.Ordinal);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"idle memory, {Tabs} tabs restored · UI process {ui} (budget {BudgetMb} MB)"));
            foreach (var helper in helpers)
            {
                Console.WriteLine("  for information: " + helper);
            }

            try
            {
                using var stream = new FileStream(trace, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                foreach (var line in reader.ReadToEnd().Split((char)10).Where(l => l.Contains("MEMORY:", StringComparison.Ordinal)))
                {
                    Console.WriteLine("  for information, from the app: " + line.Trim());
                }
            }
            catch (IOException)
            {
                // No trace line; the numbers above stand.
            }

            return ui.EndsWith("PASS", StringComparison.Ordinal) ? 0 : 1;
        }
        finally
        {
            foreach (var other in Process.GetProcessesByName("Leviathan.Worker").Concat(Process.GetProcessesByName("Leviathan.ShellHost")).Where(p => ColdStartGate.IsUnder(p, Path.GetDirectoryName(exe)!)))
            {
                other.Kill();
            }

            await Task.Delay(500);
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    private static string Describe(Process process)
    {
        var workingSet = process.WorkingSet64 / 1048576.0;
        var priv = process.PrivateMemorySize64 / 1048576.0;
        return string.Create(CultureInfo.InvariantCulture, $"working set {workingSet:0} MB, private {priv:0} MB · {(workingSet <= BudgetMb ? "PASS" : "FAIL")}");
    }

    private static async Task WriteSessionAsync(string data, List<string> folders)
    {
        var first = LocationPath.Parse(folders[0]);
        var providers = new LocationProviderRegistry([new LocalLocationProvider()]);
        var services = new TabServices(providers, new ImmediateUi(), new FolderViewRules(), TimeProvider.System, () => first);
        var manager = new WindowManager(services);
        var window = manager.OpenWindow(first, new WindowBounds(new ScreenRect(100, 100, 1400, 900)));
        foreach (var folder in folders.Skip(1))
        {
            window.Tabs.NewTab(LocationPath.Parse(folder));
        }

        var state = SessionCapture.Capture(manager);
        using var store = new JsonFileStore<SessionFile>(new FileSystem(), Path.Combine(data, "session.json"));
        await store.SaveAsync(new SessionFile(SessionFile.CurrentSchemaVersion, DateTimeOffset.UtcNow, state), CancellationToken.None);
    }
}
