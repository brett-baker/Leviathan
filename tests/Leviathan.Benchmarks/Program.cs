using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Providers.Local;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;
using Leviathan.ViewModels.Windows;

// The NAS budgets (spec 3.1), run by hand against a share you name: --nas-rows <folder>, --nas-copy <folder> [MiB].
var nasRows = Array.IndexOf(args, "--nas-rows");
if (nasRows >= 0)
{
    return nasRows + 1 < args.Length ? NasGate.Rows(args[nasRows + 1]) : Usage();
}

var nasCopy = Array.IndexOf(args, "--nas-copy");
if (nasCopy >= 0)
{
#if WINDOWS_TFM
    return nasCopy + 1 < args.Length
        ? await NasGate.CopyAsync(args[nasCopy + 1], nasCopy + 2 < args.Length && long.TryParse(args[nasCopy + 2], CultureInfo.InvariantCulture, out var mib) ? mib : 2048, args.Contains("--verify"))
        : Usage();
#else
    Console.Error.WriteLine("--nas-copy needs Windows (it measures the Windows copier).");
    return 2;
#endif
}

static int Usage()
{
    Console.Error.WriteLine("--nas-rows <folder>          first rows of a listing (budget 400 ms for 10,000 items); read only");
    Console.Error.WriteLine("--nas-copy <folder> [MiB] [--verify]   copy one large file into a new sub folder of <folder> (budget 105 MB/s); default 2048 MiB, --verify adds the read-back check");
    return 64;
}

if (args.Contains("--media-gate"))
{
    // Preview start, thumbnails and triage decisions of Phase 3 (spec 3.1) on their own.
    return await MediaGate.RunAsync();
}

if (args.Contains("--gate"))
{
    // The listing gate of Phase 0, then the view-model gates of Phase 1 and the media gates of Phase 3 (spec 3.1); --cold-start <Leviathan.exe> adds the real app's start.
    var exit = EnumerationGate.Run(args);
    exit |= ViewModelGate.Run();
    exit |= await MediaGate.RunAsync();
    var at = Array.IndexOf(args, "--cold-start");
    if (at >= 0 && at + 1 < args.Length)
    {
        exit |= ColdStartGate.Run(args[at + 1]);
    }

    return exit;
}

BenchmarkSwitcher.FromAssembly(typeof(EnumerationBenchmarks).Assembly).Run(args);
return 0;

/// <summary>Creates a temporary folder with <paramref name="count"/> empty files.</summary>
internal static class Fixture
{
    public static string CreateFolder(int count)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"leviathan-bench-{count}");
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder).Take(count + 1).Count() == count)
        {
            return folder;
        }

        Directory.CreateDirectory(folder);
        for (var i = 0; i < count; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"clip_{i:D6}.mkv"), []);
        }

        return folder;
    }

    public static (LocalLocationProvider Provider, LocationPath Location) Provider(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            return (new LocalLocationProvider(), LocationPath.Parse(folder));
        }

        // Off Windows, map a fake drive path onto the folder so the same code path is measured.
        var location = LocationPath.Parse(@"C:\bench");
        return (new LocalLocationProvider(null, null, _ => folder, TimeProvider.System), location);
    }
}

/// <summary>
/// Phase 0 gate (spec §21): a 10,000-item local folder shows its first rows in under 100 ms (p95).
/// Exit code 0 = pass, 1 = fail.
/// </summary>
internal static class EnumerationGate
{
    public static int Run(string[] args)
    {
        const int items = 10_000;
        const int runs = 40;
        var budget = TimeSpan.FromMilliseconds(100);
        var folder = Fixture.CreateFolder(items);
        var (provider, location) = Fixture.Provider(folder);

        var firstBatch = new List<double>();
        var full = new List<double>();
        for (var run = 0; run < runs; run++)
        {
            var sw = Stopwatch.StartNew();
            var first = -1.0;
            var count = 0;
            foreach (var batch in provider.ListAsync(location, ListingOptions.Default, CancellationToken.None).ToBlockingEnumerable())
            {
                if (first < 0)
                {
                    first = sw.Elapsed.TotalMilliseconds;
                }

                count += batch.Items.Count;
            }

            if (count != items)
            {
                Console.Error.WriteLine($"Expected {items} items, got {count}.");
                return 1;
            }

            firstBatch.Add(first);
            full.Add(sw.Elapsed.TotalMilliseconds);
        }

        // Discard warm-up runs.
        var p95First = Percentile(firstBatch.Skip(5).ToList(), 0.95);
        var p95Full = Percentile(full.Skip(5).ToList(), 0.95);
        var pass = p95First <= budget.TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{items:N0} items · first rows p95 {p95First:0.0} ms (budget {budget.TotalMilliseconds} ms) · full listing p95 {p95Full:0.0} ms · {(pass ? "PASS" : "FAIL")}"));
        return pass ? 0 : 1;
    }

    private static double Percentile(List<double> values, double p)
    {
        values.Sort();
        var index = (int)Math.Ceiling(p * values.Count) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }
}

/// <summary>BenchmarkDotNet suite for listing performance.</summary>
// BenchmarkDotNet discovers this type by reflection and the file uses top-level statements, so it stays in the global namespace.
#pragma warning disable CA1050
[MemoryDiagnoser]
public class EnumerationBenchmarks
{
    private LocalLocationProvider _provider = null!;
    private LocationPath _location = null!;

    [Params(1_000, 10_000, 100_000)]
    public int Items { get; set; }

    [GlobalSetup]
    public void Setup() => (_provider, _location) = Fixture.Provider(Fixture.CreateFolder(Items));

    [Benchmark]
    public async Task<int> FirstBatch()
    {
        await foreach (var batch in _provider.ListAsync(_location, ListingOptions.Default, CancellationToken.None))
        {
            return batch.Items.Count;
        }

        return 0;
    }

    [Benchmark]
    public async Task<int> FullListing()
    {
        var count = 0;
        await foreach (var batch in _provider.ListAsync(_location, ListingOptions.Default, CancellationToken.None))
        {
            count += batch.Items.Count;
        }

        return count;
    }
}


/// <summary>Runs queued work at once, on the calling thread: the numbers are those of the view model, not of a dispatcher.</summary>
internal sealed class ImmediateUi : IUiContext
{
    public void Post(Action action) => action();
}

/// <summary>
/// Phase 1 budgets through the view model (spec 3.1, build plan H2): the first rows of a 10,000-item local folder reach the UI queue in
/// under 100 ms, also when they come from the listing cache; a new tab shows its first rows in under 150 ms. Exit code 0 = all pass.
/// </summary>
internal static class ViewModelGate
{
    private const int Runs = 40;
    private const int Warmup = 5;

    public static int Run()
    {
        var folder = Fixture.CreateFolder(10_000);
        var small = Fixture.CreateFolder(1_000);
        var failed = 0;
        failed += Report("10,000 items, first rows through the view model", 100, FirstRows(folder, useCache: false));
        failed += Report("10,000 items, first rows from the listing cache", 100, FirstRows(folder, useCache: true));
        failed += Report("new tab with 1,000 items, first rows", 150, NewTab(small));
        return failed == 0 ? 0 : 1;
    }

    private static List<double> FirstRows(string folder, bool useCache)
    {
        var (provider, location) = Fixture.Provider(folder);
        var providers = new LocationProviderRegistry([provider]);
        var cache = useCache ? new ListingCache() : null;
        var times = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            using var view = new FolderViewModel(providers, new ImmediateUi(), TimeProvider.System, cache);
            view.LoadAsync(location, CancellationToken.None).GetAwaiter().GetResult();
            view.Revalidation.GetAwaiter().GetResult();

            // With a cache the first visit fills it (and lists afresh); the visits after it start from the cached rows, which is what is measured.
            if (useCache && run == 0)
            {
                continue;
            }

            times.Add(view.FirstRowsElapsed?.TotalMilliseconds ?? double.NaN);
        }

        return times;
    }

    private static List<double> NewTab(string folder)
    {
        var (provider, location) = Fixture.Provider(folder);
        var services = new TabServices(new LocationProviderRegistry([provider]), new ImmediateUi(), new FolderViewRules(), TimeProvider.System, () => location);
        var window = new WindowManager(services).OpenWindow(location);
        var times = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            var started = Stopwatch.GetTimestamp();
            var tab = window.Tabs.NewTab(location);
            tab.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            window.Tabs.Close(tab);
        }

        return times;
    }

    private static int Report(string what, double budgetMs, List<double> times)
    {
        var p95 = Percentile([.. times.Skip(Warmup)], 0.95);
        var pass = p95 <= budgetMs;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{what} · p95 {p95:0.0} ms (budget {budgetMs} ms) · {(pass ? "PASS" : "FAIL")}"));
        return pass ? 0 : 1;
    }

    private static double Percentile(List<double> values, double p)
    {
        values.Sort();
        return values[Math.Clamp((int)Math.Ceiling(p * values.Count) - 1, 0, values.Count - 1)];
    }
}

/// <summary>
/// Cold start of the real app (spec 3.1: under 600 ms to the first folder): starts Leviathan.exe several times with an empty data
/// folder and reads the "READY: cold start N ms" line of its UI trace. Needs a desktop (a window opens); run it on a quiet machine
/// with a Release build. Exit code 0 = pass.
/// </summary>
internal static class ColdStartGate
{
    private const int Runs = 6;

    public static int Run(string exe)
    {
        var times = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            var data = Path.Combine(Path.GetTempPath(), "leviathan-coldstart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            var trace = Path.Combine(data, "ui-trace.txt");
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            info.Environment["LEVIATHAN_DATA_DIR"] = data;
            info.Environment["LEVIATHAN_UI_TRACE"] = trace;
            using var process = Process.Start(info)!;
            var ready = WaitForReady(trace, TimeSpan.FromSeconds(30));
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Gone already.
            }

            foreach (var helper in new[] { "Leviathan.Worker", "Leviathan.ShellHost" })
            {
                foreach (var other in Process.GetProcessesByName(helper).Where(p => IsUnder(p, Path.GetDirectoryName(exe)!)))
                {
                    other.Kill();
                }
            }

            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }

            if (ready is null)
            {
                Console.Error.WriteLine("The app did not report READY within 30 s.");
                return 1;
            }

            times.Add(ready.Value);
        }

        times.Sort();
        var median = times[times.Count / 2];
        var worst = times[^1];
        var pass = median <= 600;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cold start to the first folder · median {median:0} ms · worst {worst:0} ms (budget 600 ms) · {(pass ? "PASS" : "FAIL")}"));
        return pass ? 0 : 1;
    }

    private static bool IsUnder(Process process, string folder)
    {
        try
        {
            return process.MainModule?.FileName.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static double? WaitForReady(string trace, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                using var stream = new FileStream(trace, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                foreach (var line in reader.ReadToEnd().Split((char)10))
                {
                    var at = line.IndexOf("READY: cold start ", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        var digits = new string([.. line[(at + 18)..].TakeWhile(char.IsDigit)]);
                        if (double.TryParse(digits, CultureInfo.InvariantCulture, out var ms))
                        {
                            return ms;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Not written yet.
            }

            Thread.Sleep(100);
        }

        return null;
    }
}
