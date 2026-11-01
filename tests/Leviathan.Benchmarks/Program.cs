using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Providers.Local;

if (args.Contains("--gate"))
{
    return EnumerationGate.Run(args);
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
