using System.Diagnostics;
using System.Globalization;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Media;
using Leviathan.Testing;
using Leviathan.ViewModels.Preview;
using Leviathan.ViewModels.Triage;

/// <summary>
/// The Phase 3 budgets (spec §3.1, build plan G3): a preview of a local file is asked for under 200 ms after the selection rests (the 150 ms of §9.1 plus a timer step), the media information of a file reads in under 300 ms (the pane asks for it off the path of the first picture), and
/// a cached thumbnail is ready in under 200 ms and a new one in under 1 s, and a triage decision moves on
/// without a stall (under 50 ms, spec §3.2). The thumbnail and preview parts need ffmpeg and ffprobe (Settings folder,
/// LEVIATHAN_FFMPEG_DIR, next to the app or PATH) and are reported as SKIPPED when they are not found; the triage part needs nothing.
/// Exit code 0 = nothing failed.
/// </summary>
internal static class MediaGate
{
    private const int Runs = 20;
    private const int Warmup = 3;

    public static async Task<int> RunAsync()
    {
        var failed = 0;
        failed += await TriageAsync();
        var tools = FfmpegLocator.Create().Locate();
        if (tools.FfprobePath is null || tools.FfmpegPath is null)
        {
            Console.WriteLine("preview start, thumbnails · SKIPPED (ffmpeg and ffprobe were not found; set LEVIATHAN_FFMPEG_DIR)");
            return failed == 0 ? 0 : 1;
        }

        var folder = Path.Combine(Path.GetTempPath(), "leviathan-bench-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var clip = Path.Combine(folder, "clip.mp4");
            if (!await MakeClipAsync(tools.FfmpegPath, clip))
            {
                Console.WriteLine("preview start, thumbnails · SKIPPED (ffmpeg could not make the test clip)");
                return failed == 0 ? 0 : 1;
            }

            failed += await PreviewStartAsync(tools, clip);
            failed += await ThumbnailsAsync(tools, clip, Path.Combine(folder, "thumbs"));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static async Task<bool> MakeClipAsync(string ffmpeg, string path)
    {
        string[] arguments = ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=6:size=320x180:rate=25", "-f", "lavfi", "-i", "sine=duration=6", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", path];
        var outcome = await new SystemProcessRunner().RunAsync(ffmpeg, arguments, TimeSpan.FromSeconds(60), 16 * 1024, CancellationToken.None);
        return outcome.ExitCode == 0 && File.Exists(path);
    }

    // Select → the preview is asked for (the 150 ms rest) → the media information of a file nobody has probed yet.
    private static async Task<int> PreviewStartAsync(FfmpegTools tools, string clip)
    {
        var info = new FileInfo(clip);
        var rest = new List<double>();
        var probe = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            using var coordinator = new PreviewCoordinator(TimeProvider.System);
            var asked = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.Requested += (_, _) => asked.TrySetResult(Stopwatch.GetTimestamp());
            var started = Stopwatch.GetTimestamp();
            coordinator.Select([Entry(clip, info.Length, info.LastWriteTimeUtc)]);
            var at = await asked.Task;
            rest.Add(Stopwatch.GetElapsedTime(started, at).TotalMilliseconds);

            using var service = new MediaMetadataService(new FfprobeRunner(new SystemProcessRunner(), () => tools), new InMemoryMediaMetadataStore());
            var before = Stopwatch.GetTimestamp();
            await service.GetAsync(new MediaFileKey(clip, info.Length, info.LastWriteTimeUtc), isNetwork: false, CancellationToken.None);
            probe.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
        }

        var total = rest.Zip(probe, (a, b) => a + b).ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  for information: rest and media information one after the other · p95 {P95(total):0} ms (the pane does not wait for the media information: it is asked for 250 ms later, off the path of the first picture)"));
        var failed = Report("preview requested after the selection rests (spec 9.1: 150 ms rest)", 200, rest);
        failed += Report("media information of a local video, cold ffprobe", 300, probe);
        return failed;
    }

    private static async Task<int> ThumbnailsAsync(FfmpegTools tools, string clip, string cacheFolder)
    {
        var info = new FileInfo(clip);
        var maker = new ThumbnailMaker(new SystemProcessRunner(), () => tools, new ThumbnailDiskCache(cacheFolder));
        using var service = new ThumbnailService(maker);
        var cachedKey = new ThumbnailKey(clip, info.Length, info.LastWriteTimeUtc.Ticks, 288);
        var first = await service.GetAsync(cachedKey, isNetwork: false, TimeSpan.FromSeconds(6), CancellationToken.None);
        if (first.Status != ThumbnailStatus.Ok)
        {
            Console.WriteLine($"thumbnails · FAIL (the first thumbnail was not made: {first.Message})");
            return 1;
        }

        var cached = new List<double>();
        var generated = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            var before = Stopwatch.GetTimestamp();
            await service.GetAsync(cachedKey, isNetwork: false, TimeSpan.FromSeconds(6), CancellationToken.None);
            cached.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);

            // A different size makes a different key, so the picture is drawn again.
            var fresh = new ThumbnailKey(clip, info.Length + run + 1, info.LastWriteTimeUtc.Ticks, 288);
            before = Stopwatch.GetTimestamp();
            var result = await service.GetAsync(fresh, isNetwork: false, TimeSpan.FromSeconds(6), CancellationToken.None);
            generated.Add(result.Status == ThumbnailStatus.Ok ? Stopwatch.GetElapsedTime(before).TotalMilliseconds : double.PositiveInfinity);
        }

        var failed = Report("thumbnail from the disk cache", 200, cached);
        failed += Report("thumbnail drawn by ffmpeg (a 6 s clip)", 1000, generated);
        return failed;
    }

    private sealed class InstantActions : ITriageActions
    {
        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // The session's own work per decision with a player and file actions that answer at once: what the view model adds to the wait (spec §10.1).
    private static async Task<int> TriageAsync()
    {
        var queue = Enumerable.Range(0, 2000).Select(i => Entry($@"C:\inbox\clip_{i:D5}.mkv", 1000 + i, DateTime.UnixEpoch.AddDays(i))).ToList();
        await using var session = new TriageSession(queue, new FakeVideoEngine(), new InstantActions(), new TriageOptions { StartOffset = 0 });
        await session.StartAsync();
        var skip = new List<double>();
        var keep = new List<double>();
        var discard = new List<double>();
        for (var i = 0; i < 600; i++)
        {
            var before = Stopwatch.GetTimestamp();
            switch (i % 3)
            {
                case 0:
                    await session.SkipAsync();
                    skip.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                    break;
                case 1:
                    await session.KeepAsync();
                    keep.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                    break;
                default:
                    await session.DiscardAsync();
                    discard.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                    break;
            }
        }

        var sort = new List<double>();
        foreach (var order in TriageListOrdering.All.Concat(TriageListOrdering.All))
        {
            var before = Stopwatch.GetTimestamp();
            await session.SortAsync(order);
            sort.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
        }

        var failed = Report("triage Skip, queue of 2,000", 50, skip);
        failed += Report("triage Keep, queue of 2,000", 50, keep);
        failed += Report("triage Discard, queue of 2,000", 50, discard);
        failed += Report("triage re-order, queue of 2,000", 50, sort, warmup: 0);
        return failed;
    }

    private static FileEntry Entry(string path, long size, DateTime modified) =>
        new(Path.GetFileName(path), LocationPath.Parse(path), FileEntryKind.File, size, modified, modified, FileAttributes.Normal);

    private static double P95(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    private static int Report(string what, double budgetMs, List<double> times, int warmup = Warmup)
    {
        var p95 = P95([.. times.Skip(warmup)]);
        var pass = p95 <= budgetMs;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{what} · p95 {p95:0.0} ms (budget {budgetMs} ms) · {(pass ? "PASS" : "FAIL")}"));
        return pass ? 0 : 1;
    }
}
