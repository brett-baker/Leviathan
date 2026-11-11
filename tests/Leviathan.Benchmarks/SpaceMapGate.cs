using System.Diagnostics;
using System.Globalization;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.SpaceMap;
using Leviathan.Testing;

/// <summary>
/// The Phase 4 space map gate (build plan): the map of a 1 TB share completes and can resume. Measured on a synthetic share of 400,000 files of 2.5 MB (about 1 TB) in 20,000
/// folders served by an in-memory provider, so it measures the scanner, the tree, the checkpoints and the layout, not a disk or a NAS. The scan is stopped half way (as a killed
/// worker would leave it: only its last checkpoint), continued from that copy, and must end with the same totals as a scan that was never stopped. The layout of a million files
/// is timed on its own. A run against the real NAS is a manual lab step (docs/phase-4.md).
/// </summary>
internal static class SpaceMapGate
{
    private const int Folders = 20_000;
    private const int FilesPerFolder = 20;
    private const long FileBytes = 2_500_000;

    public static async Task<int> RunAsync()
    {
        var failed = 0;
        var files = new FakeLocationProvider();
        for (var f = 0; f < Folders; f++)
        {
            for (var i = 0; i < FilesPerFolder; i++)
            {
                files.AddFile($@"\\nas\media\f{f}\file{i}.mkv", FileBytes + i);
            }
        }

        var registry = new LocationProviderRegistry([files]);
        var root = LocationPath.Parse(@"\\nas\media");
        var expected = (long)Folders * FilesPerFolder * FileBytes + ((long)Folders * (FilesPerFolder * (FilesPerFolder - 1) / 2));

        var started = Stopwatch.StartNew();
        using var whole = new SpaceScanner(registry, null, TimeProvider.System, new SpaceScanOptions { Parallelism = 8, ParallelismPerShare = 8 }).Start(root);
        await whole.Completion;
        var wholeTime = started.Elapsed;
        var okTotal = whole.Root.Size == expected && whole.Root.Files == Folders * FilesPerFolder;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  synthetic share: {whole.Root.Files:N0} files, {whole.Root.Size / 1e12:0.00} TB, scanned in {wholeTime.TotalSeconds:0.0} s"));
        failed += Report("space map of a ~1 TB share completes with the right totals", okTotal);

        // Stopped half way: only the last copy survives, the way a killed worker leaves it.
        SpaceScanSnapshot? checkpoint = null;
        using var doomed = new SpaceScanner(registry, null, TimeProvider.System, new SpaceScanOptions { Parallelism = 8, ParallelismPerShare = 8 }).Start(root);
        doomed.Changed += (_, _) =>
        {
            if (checkpoint is null && doomed.Progress.FoldersListed >= Folders / 2)
            {
                checkpoint = doomed.Snapshot(DateTimeOffset.UtcNow, complete: false);
            }
        };
        await doomed.Completion;
        var bytes = SpaceScanCodec.Encode(checkpoint!);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  checkpoint at half way: {checkpoint!.Rows.Count:N0} rows, {bytes.Length / 1024.0 / 1024.0:0.0} MB compressed"));
        var restored = SpaceScanCodec.Decode(bytes)!;
        files.ListCalls.Clear();
        started.Restart();
        using var resumed = new SpaceScanner(registry, null, TimeProvider.System, new SpaceScanOptions { Parallelism = 8, ParallelismPerShare = 8 }).Start(root, restored);
        await resumed.Completion;
        var resumeTime = started.Elapsed;
        var listedAgain = files.ListCalls.Count;
        failed += Report("a scan resumed from half way ends with the same totals", resumed.Root.Size == expected && resumed.Root.Files == Folders * FilesPerFolder);
        failed += Report(string.Create(CultureInfo.InvariantCulture, $"a resumed scan lists only what was left ({listedAgain:N0} of {Folders + 1:N0} folders, in {resumeTime.TotalSeconds:0.0} s)"), listedAgain < (Folders * 6 / 10));

        // The layout of the whole tree at full screen size.
        var times = new List<double>();
        for (var run = 0; run < 6; run++)
        {
            var layoutStarted = Stopwatch.StartNew();
            var tiles = Treemap.Layout(whole.Root, new TileRect(0, 0, 2560, 1440));
            times.Add(layoutStarted.Elapsed.TotalMilliseconds);
            if (run == 0)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  layout: {tiles.Count:N0} tiles for {whole.Root.Files:N0} files"));
            }
        }

        var sorted = times.Skip(1).Order().ToList();
        var p95 = sorted[^1];
        failed += Report(string.Create(CultureInfo.InvariantCulture, $"space map layout of {whole.Root.Files:N0} files at 2560x1440 · p95 {p95:0} ms (budget 1000 ms)"), p95 <= 1000);
        return failed == 0 ? 0 : 1;
    }

    private static int Report(string what, bool pass)
    {
        Console.WriteLine($"{what} · {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
