using System.Diagnostics;
using System.Globalization;
using Leviathan.Core.Paths;
using Leviathan.Search;
using Leviathan.Testing;

/// <summary>
/// The Phase 4 search budgets (spec §3.1, build plan Phase 4 gate): a local name search answers in under 50 ms. Measured here on the in-memory name
/// index with a million synthetic names (the same structure serves the local fallback and the NAS index); the Everything source needs a machine with
/// Everything and is reported as SKIPPED. Exit code 0 = nothing failed.
/// </summary>
internal static class SearchGate
{
    private const int Entries = 1_000_000;
    private const int FilesPerFolder = 50;
    private const int Runs = 20;
    private const int Warmup = 3;

    public static async Task<int> RunAsync()
    {
        var random = new Random(7);
        string[] words = ["holiday", "report", "invoice", "summer", "backup", "draft", "final", "photo", "clip", "notes", "archive", "project", "scan", "music", "movie", "random", "alpha", "beta", "gamma", "delta"];
        string[] extensions = ["mkv", "mp4", "txt", "jpg", "pdf", "zip", "cs", "docx", "mp3", "png"];
        using var index = new NameIndex();
        var root = index.AddRoot(LocationPath.Parse(@"D:\"));
        var build = Stopwatch.StartNew();
        var folders = Entries / FilesPerFolder;
        var top = index.ReplaceChildren(root, [.. Enumerable.Range(0, 100).Select(i => new IndexedChild("dir" + i.ToString(CultureInfo.InvariantCulture), true, 0, 1, FileAttributes.Directory))]);
        var made = 0;
        for (var f = 0; f < folders; f++)
        {
            var parent = top[f % top.Length];
            var folderId = index.ReplaceChildren(parent, [.. index.ChildrenOf(parent).Select(index.Describe), new IndexedChild("sub" + f.ToString(CultureInfo.InvariantCulture), true, 0, 1, FileAttributes.Directory)]);
            var files = new IndexedChild[FilesPerFolder];
            for (var i = 0; i < FilesPerFolder; i++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"{words[random.Next(words.Length)]}_{words[random.Next(words.Length)]}_{f}_{i}.{extensions[random.Next(extensions.Length)]}");
                files[i] = new IndexedChild(name, false, random.Next(0, 1_000_000_000), 638_000_000_000_000_000L + random.Next(), FileAttributes.Normal);
            }

            index.ReplaceChildren(folderId[^1], files);
            made += FilesPerFolder;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  index built: {index.Count:N0} entries in {build.Elapsed.TotalSeconds:0.0} s ({GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024)} MB managed)"));
        var failed = 0;

        // A word that few names hold, a pair of words, a filter by kind and size, and a word that matches thousands of names.
        failed += await Report(index, "a rare word (one name in a million, \"_12345_7.\")", "_12345_7.", 50);
        failed += await Report(index, "a word and an extension (\"scan ext:mkv\")", "scan ext:mkv", 50);
        failed += await Report(index, "a word, excluding another (\"holiday -draft\"), 100,000 names hold the word", "holiday -draft", 50);
        failed += await Report(index, "a kind and a size (\"type:video size:>900MB\")", "type:video size:>900MB", 50);
        Console.WriteLine("local name search through Everything · SKIPPED (needs a machine with Everything installed)");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> Report(NameIndex index, string what, string text, double budgetMs, bool informational = false)
    {
        var first = new List<double>();
        var total = new List<double>();
        var count = 0;
        for (var run = 0; run < Runs; run++)
        {
            var query = QueryParser.Parse(text);
            var matcher = new QueryMatcher(query, TimeProvider.System, TimeZoneInfo.Utc);
            var started = Stopwatch.GetTimestamp();
            count = 0;
            double? firstAt = null;
            await foreach (var batch in new NameIndexSearch(index).RunAsync(query, [], matcher, NoSearchFacts.Instance, CancellationToken.None))
            {
                if (batch.Matches.Count > 0)
                {
                    firstAt ??= Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }

                count += batch.Matches.Count;
            }

            var done = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            first.Add(firstAt ?? done);
            total.Add(done);
        }

        var p95First = P95(first);
        var p95Total = P95(total);
        var pass = informational || p95First <= budgetMs;
        var verdict = informational ? "FOR INFORMATION" : pass ? "PASS" : "FAIL";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"search the name index, {what} · {count:N0} found · first results p95 {p95First:0.0} ms (budget {budgetMs} ms) · all of them p95 {p95Total:0.0} ms · {verdict}"));
        return pass ? 0 : 1;
    }

    private static double P95(List<double> times)
    {
        var sorted = times.Skip(Warmup).Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
