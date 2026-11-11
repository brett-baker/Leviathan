using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.Search.Core.Tests;

/// <summary>Keeping the chosen folders indexed: saved between runs, listed when due, held back politely (spec §13, build plan C).</summary>
public sealed class IndexManagerTests : IDisposable
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class MemoryStore : INameIndexStore
    {
        public IndexSnapshot? Saved { get; set; }

        public int Saves { get; private set; }

        public void Save(IndexSnapshot snapshot)
        {
            Saved = snapshot;
            Saves++;
        }

        public IndexSnapshot? Load() => Saved;
    }

    private sealed class MemoryFolders(params LocationPath[] initial) : IIndexedFolders
    {
        public List<LocationPath> Saved { get; set; } = [.. initial];

        public IReadOnlyList<LocationPath> Load() => Saved;

        public void Save(IReadOnlyList<LocationPath> folders) => Saved = [.. folders];
    }

    private readonly NameIndex _index = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLocationProvider _files = new FakeLocationProvider()
        .AddFile(@"\\nas\media\inbox\a.mkv", 10)
        .AddFile(@"\\nas\media\inbox\b.mkv", 20)
        .AddFile(@"\\nas\media\other\c.txt", 30);

    public void Dispose() => _index.Dispose();

    private IndexManager Manager(MemoryStore store, MemoryFolders folders, Func<LocationPath, bool>? busy = null, IndexSchedule? schedule = null) =>
        new(_index, new LocationProviderRegistry([_files]), store, folders, _time, schedule ?? new IndexSchedule { CheckEvery = TimeSpan.FromMinutes(1), ResumeCheckEvery = TimeSpan.FromSeconds(1) }, busy);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what);
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task CrawlNow_ListsTheFolder_MarksItCovered_AndSavesTheIndex()
    {
        var store = new MemoryStore();
        using var manager = Manager(store, new MemoryFolders());
        manager.Add(P(@"\\nas\media"));

        await manager.CrawlNowAsync(P(@"\\nas\media"), CancellationToken.None);

        Assert.Equal(_time.GetUtcNow(), _index.CoveredSince(P(@"\\nas\media\inbox")));
        Assert.Equal(1, store.Saves);
        Assert.Equal(6, store.Saved!.Rows.Count); // the root, inbox, other, a, b and c
        var status = Assert.Single(manager.Status());
        Assert.False(status.IsCrawling);
        Assert.Equal(_time.GetUtcNow(), status.LastCrawled);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(6), status.NextCrawl);
    }

    [Fact]
    public async Task Initialize_LoadsTheChosenFoldersAndTheSavedIndex_SoSearchWorksAtOnce()
    {
        var store = new MemoryStore();
        using (var first = Manager(store, new MemoryFolders(P(@"\\nas\media"))))
        {
            first.Initialize();
            await first.CrawlNowAsync(P(@"\\nas\media"), CancellationToken.None);
        }

        using var again = new NameIndex();
        using var second = new IndexManager(again, new LocationProviderRegistry([new FakeLocationProvider()]), store, new MemoryFolders(P(@"\\nas\media")), _time);
        second.Initialize();

        Assert.NotNull(again.CoveredSince(P(@"\\nas\media\inbox")));
        Assert.Equal(5, again.Count);
        Assert.Single(again.Candidates("c.txt", [], null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_DropsIndexedFoldersTheUserNoLongerChose()
    {
        var store = new MemoryStore();
        using (var first = Manager(store, new MemoryFolders(P(@"\\nas\media"))))
        {
            first.Initialize();
            await first.CrawlNowAsync(P(@"\\nas\media"), CancellationToken.None);
        }

        using var again = new NameIndex();
        using var second = new IndexManager(again, new LocationProviderRegistry([_files]), store, new MemoryFolders(P(@"\\nas\other")), _time);
        second.Initialize();

        Assert.Empty(again.Roots);
        Assert.Equal(0, again.Count);
    }

    [Fact]
    public void Initialize_ADamagedSavedIndex_IsIgnored()
    {
        var store = new MemoryStore { Saved = new IndexSnapshot([new IndexRow(0, 5, "x", false, 0, 0, 0)], [new IndexRootState(0, null)]) };
        using var manager = Manager(store, new MemoryFolders(P(@"\\nas\media")));

        manager.Initialize();

        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public async Task Remove_StopsIndexing_AndDropsTheEntries()
    {
        var store = new MemoryStore();
        var folders = new MemoryFolders();
        using var manager = Manager(store, folders);
        manager.Add(P(@"\\nas\media"));
        await manager.CrawlNowAsync(P(@"\\nas\media"), CancellationToken.None);

        manager.Remove(P(@"\\nas\media"));

        Assert.Empty(folders.Saved);
        Assert.Empty(manager.Roots);
        Assert.Equal(0, _index.Count);
        Assert.Null(_index.CoveredSince(P(@"\\nas\media")));
        Assert.False(manager.IsIndexed(P(@"\\nas\media\inbox")));
    }

    [Fact]
    public void Add_IsSavedOnce_AndKnownToIsIndexed()
    {
        var folders = new MemoryFolders();
        using var manager = Manager(new MemoryStore(), folders);

        manager.Add(P(@"\\nas\media"));
        manager.Add(P(@"\\nas\media"));

        Assert.Equal([P(@"\\nas\media")], folders.Saved);
        Assert.True(manager.IsIndexed(P(@"\\nas\media\inbox\a.mkv")));
        Assert.False(manager.IsIndexed(P(@"\\nas\other")));
    }

    [Fact]
    public async Task Run_ListsADueFolder_AndAgainWhenTheScheduleSaysSo_ButNotBefore()
    {
        var store = new MemoryStore();
        var folders = new MemoryFolders(P(@"\\nas\media"));
        using var manager = Manager(store, folders);
        manager.Initialize();
        using var cts = new CancellationTokenSource();
        var run = manager.RunAsync(cts.Token);

        await Eventually(() => store.Saves == 1, "the first listing");
        await Eventually(() => _time.Pending > 0, "the manager to wait for its next check");
        _files.AddFile(@"\\nas\media\inbox\new.mkv", 1);
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(1, store.Saves); // not due yet

        _time.Advance(TimeSpan.FromHours(6));
        await Eventually(() => store.Saves == 2, "the second listing");
        Assert.Single(_index.Candidates("new.mkv", [], null, 10, CancellationToken.None));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Run_WhileAJobCopiesToTheSameShare_WaitsAndThenGoesOn()
    {
        var busy = true;
        var store = new MemoryStore();
        using var manager = Manager(store, new MemoryFolders(P(@"\\nas\media")), busy: _ => Volatile.Read(ref busy));
        manager.Initialize();
        using var cts = new CancellationTokenSource();
        var run = manager.RunAsync(cts.Token);

        await Eventually(() => _time.Pending > 0, "the manager to wait");
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        Assert.Equal(0, store.Saves);

        Volatile.Write(ref busy, false);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Eventually(() => store.Saves == 1, "the listing after the job ended");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ACrawlThatBecomesBusyHalfWay_WaitsWhereItIs_AndSaysWhy()
    {
        var busy = false;
        var store = new MemoryStore();
        var folders = new MemoryFolders(P(@"\\nas\media"));
        using var manager = Manager(store, folders, busy: f => Volatile.Read(ref busy) && f.Segments.Count > 0 && f.Segments[^1] == "inbox");
        manager.Initialize();
        Volatile.Write(ref busy, true);

        var crawl = manager.CrawlNowAsync(P(@"\\nas\media"), CancellationToken.None);
        await Eventually(() => manager.Status()[0].IsWaiting, "the crawl to hold back");
        Assert.Contains("copies", manager.Status()[0].Message, StringComparison.Ordinal);
        Assert.False(crawl.IsCompleted);

        Volatile.Write(ref busy, false);
        for (var i = 0; i < 20 && !crawl.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        await crawl;
        Assert.False(manager.Status()[0].IsWaiting);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task Pause_HoldsBackScheduledListings()
    {
        var store = new MemoryStore();
        using var manager = Manager(store, new MemoryFolders(P(@"\\nas\media")));
        manager.Initialize();
        manager.SetPaused(true);
        using var cts = new CancellationTokenSource();
        var run = manager.RunAsync(cts.Token);

        await Eventually(() => _time.Pending > 0, "the manager to wait");
        _time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50);
        Assert.Equal(0, store.Saves);
        Assert.True(manager.IsPaused);

        manager.SetPaused(false);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Eventually(() => store.Saves == 1, "the listing after resuming");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task OnFolderListed_FoldsWhatTheUserSawIntoTheIndex()
    {
        using var manager = Manager(new MemoryStore(), new MemoryFolders());
        manager.Add(P(@"\\nas\media"));
        await manager.CrawlNowAsync(P(@"\\nas\media"), CancellationToken.None);
        var now = new DateTimeOffset(2026, 11, 10, 13, 0, 0, TimeSpan.Zero);
        var seen = new List<FileEntry>
        {
            new("a.mkv", P(@"\\nas\media\inbox\a.mkv"), FileEntryKind.File, 10, now, now, FileAttributes.Normal),
            new("fresh.mkv", P(@"\\nas\media\inbox\fresh.mkv"), FileEntryKind.File, 99, now, now, FileAttributes.Normal),
        };

        Assert.True(manager.OnFolderListed(P(@"\\nas\media\inbox"), seen));

        Assert.Single(_index.Candidates("fresh.mkv", [], null, 10, CancellationToken.None));
        Assert.Empty(_index.Candidates("b.mkv", [], null, 10, CancellationToken.None)); // gone from the folder
        Assert.False(manager.OnFolderListed(P(@"C:\elsewhere"), seen));
        Assert.False(manager.OnFolderListed(P(@"\\nas\media\never-listed"), seen));
    }

    [Fact]
    public async Task ExportThenImport_GivesTheSameIndex_WithoutTheDeadEntries()
    {
        var folders = new MemoryFolders();
        using var manager = Manager(new MemoryStore(), folders);
        manager.Add(P(@"\\nas\media"));
        await manager.CrawlNowAsync(P(@"\\nas\media"), CancellationToken.None);
        _files.AddFile(@"\\nas\media\inbox\late.mkv", 1);
        _index.Remove(_index.Find(P(@"\\nas\media\other")));
        var snapshot = _index.Export();

        using var copy = new NameIndex();
        copy.Import(snapshot);

        Assert.Equal(_index.Count, copy.Count);
        Assert.All(snapshot.Rows, r => Assert.True(r.Parent < r.Id));
        Assert.Equal(_index.Candidates("mkv", [], null, 100, CancellationToken.None).Select(e => e.Location.ToString()).Order(),
            copy.Candidates("mkv", [], null, 100, CancellationToken.None).Select(e => e.Location.ToString()).Order());
        Assert.NotNull(copy.CoveredSince(P(@"\\nas\media\inbox")));
        Assert.Throws<InvalidOperationException>(() => copy.Import(snapshot));
    }
}
