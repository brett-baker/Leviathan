using System.Diagnostics;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class FolderItemsViewTests
{
    /// <summary>A fixed "now" in UTC so date buckets do not depend on the machine's time zone.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    // Wednesday 2026-11-04 12:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly LocationPath Root = LocationPath.Parse(@"C:\x");

    private static FileEntry File(string name, long size = 1, DateTimeOffset? modified = null) =>
        new(name, Root.Combine(name), FileEntryKind.File, size, modified ?? Now, modified ?? Now, FileAttributes.Normal);

    private static FileEntry Folder(string name) =>
        new(name, Root.Combine(name), FileEntryKind.Folder, 0, Now, Now, FileAttributes.Directory);

    private static (RangeObservableCollection<FileEntry> Source, FolderItemsView View) Make(params FileEntry[] items)
    {
        var source = new RangeObservableCollection<FileEntry>();
        var view = new FolderItemsView(source, time: new FixedTime(Now));
        source.AddRange(items);
        return (source, view);
    }

    private static string[] Names(FolderItemsView view) => [.. view.Rows.Select(r => r.Name)];

    [Fact]
    public void DefaultSort_IsNaturalByName_FoldersFirst()
    {
        var (_, view) = Make(File("file10.txt"), File("file2.txt"), Folder("zeta"), File("File1.txt"), Folder("alpha"));

        Assert.Equal(["alpha", "zeta", "File1.txt", "file2.txt", "file10.txt"], Names(view));
    }

    [Theory]
    [InlineData(FolderPlacement.Mixed, new[] { "a.txt", "b", "c.txt", "d" })]
    [InlineData(FolderPlacement.Last, new[] { "a.txt", "c.txt", "b", "d" })]
    [InlineData(FolderPlacement.First, new[] { "b", "d", "a.txt", "c.txt" })]
    public void FolderPlacement_ControlsWhereFoldersSort(FolderPlacement placement, string[] expected)
    {
        var (_, view) = Make(File("a.txt"), Folder("b"), File("c.txt"), Folder("d"));

        view.SetFolderPlacement(placement);

        Assert.Equal(expected, Names(view));
    }

    [Fact]
    public void SortBy_SameColumnTwice_FlipsDirection_DifferentColumnResetsToAscending()
    {
        var (_, view) = Make(File("a", size: 30), File("b", size: 10), File("c", size: 20));

        view.SortBy("size");
        Assert.Equal(["b", "c", "a"], Names(view));
        view.SortBy("size");
        Assert.Equal(["a", "c", "b"], Names(view));
        view.SortBy("name");
        Assert.Equal(["a", "b", "c"], Names(view));
        Assert.Equal([new SortKey("name")], view.Sort);
    }

    [Fact]
    public void SortBy_Additive_AddsASecondaryKey_ThenFlipsIt()
    {
        var (_, view) = Make(File("b", size: 1), File("a", size: 1), File("c", size: 0));
        view.SortBy("size");

        view.SortBy("name", additive: true);
        Assert.Equal(["c", "a", "b"], Names(view));

        view.SortBy("name", additive: true);
        Assert.Equal(["c", "b", "a"], Names(view));
        Assert.Equal(2, view.Sort.Count);
    }

    [Fact]
    public void SortDescending_DoesNotMoveFoldersBelowFiles()
    {
        var (_, view) = Make(File("a"), File("b"), Folder("z"));

        view.SortBy("name");

        Assert.Equal(["z", "b", "a"], Names(view));
    }

    [Fact]
    public void BatchesArrivingOverTime_ProduceTheSameOrderAsOneFullSort()
    {
        var random = new Random(42);
        var items = Enumerable.Range(0, 3000)
            .Select(i => random.Next(5) == 0 ? Folder("dir" + random.Next(500)) : File("file" + random.Next(1000) + (random.Next(3) == 0 ? ".mkv" : ".txt"), random.Next(100)))
            .DistinctBy(e => e.Location.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var (source, streamed) = Make();
        streamed.SortBy("size");
        streamed.SortBy("name", additive: true);
        for (var i = 0; i < items.Count; i += 137)
        {
            source.AddRange(items.Skip(i).Take(137).ToList());
        }

        var oneShot = new RangeObservableCollection<FileEntry>();
        var full = new FolderItemsView(oneShot, new FolderViewSettings { Sort = streamed.Sort });
        oneShot.AddRange(items);

        Assert.Equal(items.Count, streamed.TotalCount);
        Assert.Equal(full.Rows.Select(r => r.Location.Value), streamed.Rows.Select(r => r.Location.Value));
    }

    [Fact]
    public void SourceReset_ClearsTheView()
    {
        var (source, view) = Make(File("a"), File("b"));

        source.Reset([]);

        Assert.Empty(view.Rows);
        Assert.Equal(0, view.TotalCount);
    }

    [Fact]
    public void ViewCreatedOverAFilledSource_ShowsItsRows()
    {
        var source = new RangeObservableCollection<FileEntry>();
        source.AddRange([File("b"), File("a")]);

        var view = new FolderItemsView(source);

        Assert.Equal(["a", "b"], Names(view));
    }

    [Fact]
    public void Apply_Settings_ChangesSortPlacementAndGrouping()
    {
        var (_, view) = Make(File("a", size: 5), File("b", size: 9), Folder("d"));

        view.Apply(new FolderViewSettings { Sort = [new SortKey("size", SortDirection.Descending)], Folders = FolderPlacement.Last });

        Assert.Equal(["b", "a", "d"], Names(view));
        Assert.Null(view.GroupBy);

        view.Apply(new FolderViewSettings { GroupBy = "name" });

        Assert.Equal("name", view.GroupBy);
        Assert.Equal(FolderPlacement.First, view.Folders);
    }

    [Theory]
    [InlineData("a.mkv", new[] { "a.mkv" })]
    [InlineData("*.mkv", new[] { "a.mkv", "c.MKV" })]
    [InlineData("-*.mkv", new[] { "b.txt", "movie night.avi" })]
    [InlineData("a?.mkv", new string[0])]
    [InlineData("\"movie night\"", new[] { "movie night.avi" })]
    [InlineData("ext:mkv,avi", new[] { "a.mkv", "c.MKV", "movie night.avi" })]
    [InlineData("size:>1KB", new[] { "c.MKV", "movie night.avi" })]
    [InlineData("size:<=1KB *.mkv", new[] { "a.mkv" })]
    [InlineData("size:2048", new[] { "movie night.avi" })]
    [InlineData("TXT", new[] { "b.txt" })]
    public void Filter_Syntax_NarrowsTheCurrentFolder(string text, string[] expected)
    {
        var (_, view) = Make(File("a.mkv", 100), File("b.txt", 100), File("c.MKV", 5000), File("movie night.avi", 2048));

        view.SetFilter(text);

        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), Names(view).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(4, view.TotalCount);
        Assert.Equal(expected.Length, view.ShownCount);
    }

    [Theory]
    [InlineData("size:abc")]
    [InlineData("duration:soon")]
    public void Filter_BadTerm_IsReportedAndIgnored(string text)
    {
        var (_, view) = Make(File("a"), File("b"));

        view.SetFilter(text);

        Assert.Single(view.Filter.Errors);
        Assert.Equal(2, view.ShownCount);
    }

    [Fact]
    public void Filter_AppliesToBatchesThatArriveLater()
    {
        var (source, view) = Make(File("a.mkv"), File("b.txt"));
        view.SetFilter("*.mkv");

        source.AddRange([File("c.mkv"), File("d.txt")]);

        Assert.Equal(["a.mkv", "c.mkv"], Names(view));
        Assert.Equal(4, view.TotalCount);
    }

    [Fact]
    public void Filter_ClearedWithEmptyText_ShowsEverythingAgain()
    {
        var (_, view) = Make(File("a.mkv"), File("b.txt"));
        view.SetFilter("*.mkv");

        view.SetFilter("");

        Assert.Equal(2, view.ShownCount);
        Assert.True(view.Filter.IsEmpty);
    }

    private sealed class FakeMetadata(Dictionary<string, (string[] Tags, TimeSpan Duration)> data) : IEntryMetadata
    {
        public IReadOnlySet<string>? GetTags(FileEntry entry) => data.TryGetValue(entry.Name, out var d) ? d.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        public TimeSpan? GetDuration(FileEntry entry) => data.TryGetValue(entry.Name, out var d) ? d.Duration : null;
    }

    [Fact]
    public void Filter_TagAndDuration_UseMetadata_AndMatchNothingWithoutIt()
    {
        var (_, view) = Make(File("a.mkv"), File("b.mkv"), File("c.mkv"));
        view.SetFilter("tag:red");
        Assert.Equal(0, view.ShownCount);

        view.SetMetadata(new FakeMetadata(new()
        {
            ["a.mkv"] = (["Red", "green"], TimeSpan.FromMinutes(1)),
            ["b.mkv"] = (["blue"], TimeSpan.FromMinutes(90)),
        }));

        Assert.Equal(["a.mkv"], Names(view));
        view.SetFilter("duration:<2m");
        Assert.Equal(["a.mkv"], Names(view));
        view.SetFilter("duration:>=1h30m");
        Assert.Equal(["b.mkv"], Names(view));
        view.SetFilter("-tag:red");
        Assert.Equal(["b.mkv", "c.mkv"], Names(view));
    }

    [Theory]
    [InlineData("1GB", 1024L * 1024 * 1024)]
    [InlineData("1.5g", 1610612736L)]
    [InlineData("500 MB", 500L * 1024 * 1024)]
    [InlineData("200k", 204800L)]
    [InlineData("42", 42L)]
    public void ParseSize_AcceptsUnits(string text, long expected)
    {
        Assert.Equal(expected, FilterQuery.ParseSize(text));
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("2m", 120)]
    [InlineData("1h30m", 5400)]
    [InlineData("1:30", 90)]
    [InlineData("1:02:03", 3723)]
    [InlineData("45", 45)]
    public void ParseDuration_AcceptsCommonForms(string text, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), FilterQuery.ParseDuration(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:xx")]
    public void ParseDuration_RejectsGarbage(string text)
    {
        Assert.Null(FilterQuery.ParseDuration(text));
        Assert.Null(FilterQuery.ParseSize(text));
    }

    [Fact]
    public void GroupBy_Size_BucketsInOrder_AndFoldersLast()
    {
        var (_, view) = Make(File("big", 2L * 1024 * 1024 * 1024), File("zero", 0), File("mid", 5 * 1024 * 1024), File("tiny", 100), Folder("dir"));

        view.SetGroupBy("size");

        Assert.Equal(["Empty", "Tiny (under 16 KB)", "Medium (under 128 MB)", "Huge (1 GB and over)", "Folders"], view.Groups.Select(g => g.Title));
        Assert.Equal(["zero", "tiny", "mid", "big", "dir"], Names(view));
    }

    [Fact]
    public void GroupBy_Modified_UsesRelativeDateBuckets()
    {
        var (_, view) = Make(
            File("today", modified: Now.AddHours(-2)),
            File("yesterday", modified: Now.AddDays(-1)),
            File("monday", modified: Now.AddDays(-2)),
            File("lastweek", modified: Now.AddDays(-8)),
            File("earlierMonth", modified: new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero)),
            File("earlierYear", modified: new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)),
            File("y2025", modified: new DateTimeOffset(2025, 3, 1, 9, 0, 0, TimeSpan.Zero)),
            File("y2023", modified: new DateTimeOffset(2023, 3, 1, 9, 0, 0, TimeSpan.Zero)));

        view.SetGroupBy("modified");

        Assert.Equal(["Today", "Yesterday", "Earlier this week", "Last week", "Earlier this year", "2025", "2023"], view.Groups.Select(g => g.Title));
    }

    [Fact]
    public void GroupBy_Descending_ReversesGroupOrderWhenSortedByTheSameColumn()
    {
        var (_, view) = Make(File("zero", 0), File("tiny", 100), File("mid", 5 * 1024 * 1024));
        view.SetGroupBy("size");
        view.SortBy("size");
        view.SortBy("size");

        Assert.Equal(["Medium (under 128 MB)", "Tiny (under 16 KB)", "Empty"], view.Groups.Select(g => g.Title));
    }

    [Fact]
    public void GroupBy_Name_UsesFirstLetter_AndSymbolsGoFirst()
    {
        var (_, view) = Make(File("apple"), File("Avocado"), File("banana"), File("_hidden"), File("7zip"));

        view.SetGroupBy("name");

        Assert.Equal(["#", "A", "B"], view.Groups.Select(g => g.Title));
        Assert.Equal(2, view.Groups.Single(g => g.Title == "A").Items.Count);
    }

    [Fact]
    public void GroupBy_NonGroupableColumn_IsIgnored()
    {
        var (_, view) = Make(File("a"));

        view.SetGroupBy("attributes");

        Assert.Null(view.GroupBy);
        Assert.Empty(view.Groups);
    }

    [Fact]
    public void CollapsedGroups_StayCollapsedWhenMoreRowsArrive()
    {
        var (source, view) = Make(File("apple"), File("banana"));
        view.SetGroupBy("name");
        view.Groups.Single(g => g.Title == "A").IsCollapsed = true;

        source.AddRange([File("avocado"), File("cherry")]);

        Assert.True(view.Groups.Single(g => g.Title == "A").IsCollapsed);
        Assert.False(view.Groups.Single(g => g.Title == "B").IsCollapsed);
        Assert.Equal(2, view.Groups.Single(g => g.Title == "A").Items.Count);
    }

    [Fact]
    public void Streaming100kItems_StaysWithinTheEnumerationBudget()
    {
        var random = new Random(7);
        var source = new RangeObservableCollection<FileEntry>();
        var view = new FolderItemsView(source);
        view.SortBy("size");
        var batches = Enumerable.Range(0, 100_000 / 256 + 1)
            .Select(b => Enumerable.Range(b * 256, 256).Take(Math.Max(0, 100_000 - b * 256)).Select(i => File("file" + i + ".dat", random.Next(1_000_000))).ToList())
            .ToList();

        var watch = Stopwatch.StartNew();
        foreach (var batch in batches)
        {
            source.AddRange(batch);
        }

        watch.Stop();

        Assert.Equal(100_000, view.TotalCount);
        // Spec §3.1 budgets a 100,000-item folder at 1.5 s including sort, and the benchmark gate (tests/Leviathan.Benchmarks) holds that
        // on a quiet machine in Release. This unit test only guards against the sort going quadratic, so its bound is wide enough for
        // Debug builds on a shared CI runner (1.95 s was seen there).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"sorting 100k streamed items took {watch.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public void SortBySize_OrdersFoldersByTheirCountedTotal_UnknownFirst_AndFilesByTheirOwnSize()
    {
        var sizes = new Dictionary<string, long> { ["big"] = 900, ["small"] = 10 };
        var (_, view) = Make(Folder("small"), Folder("big"), Folder("unknown"), File("f.bin", 500), File("g.bin", 5));
        view.SetFolderMetrics((e, m) => m == FolderMetric.TotalBytes && sizes.TryGetValue(e.Name, out var v) ? v : null);

        view.SortBy("size");

        Assert.Equal(["unknown", "small", "big", "g.bin", "f.bin"], Names(view));
        view.SortBy("size");
        Assert.Equal(["big", "small", "unknown", "f.bin", "g.bin"], Names(view));
    }

    [Fact]
    public void ResortByMetrics_MovesAFolderWhenItsNumberArrives_AndDoesNothingForOtherSorts()
    {
        var sizes = new Dictionary<string, long> { ["a"] = 10, ["b"] = 20 };
        var (_, view) = Make(Folder("a"), Folder("b"));
        view.SetFolderMetrics((e, m) => m == FolderMetric.Items && sizes.TryGetValue(e.Name, out var v) ? v : null);
        view.SortBy("items");
        Assert.Equal(["a", "b"], Names(view));
        var changes = 0;
        view.Rows.CollectionChanged += (_, _) => changes++;

        view.ResortByMetrics();
        Assert.Equal(0, changes);

        sizes["a"] = 99;
        view.ResortByMetrics();
        Assert.Equal(["b", "a"], Names(view));

        view.SortBy("name");
        sizes["b"] = 1000;
        var before = Names(view);
        view.ResortByMetrics();
        Assert.Equal(before, Names(view));
    }
}
