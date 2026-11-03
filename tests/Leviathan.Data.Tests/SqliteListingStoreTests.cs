using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Data;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Views;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteListingStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-listings-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteListingStore> _stores = [];
    private static readonly DateTimeOffset Stamp = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly LocationPath Folder = LocationPath.Parse(@"C:\media\films");
    private static readonly ListingKey Key = new(Folder, false, false);

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string DbPath => Path.Combine(_folder, "listings.db");

    private SqliteListingStore Open(long maxBytes = SqliteListingStore.DefaultMaxBytes, TimeProvider? time = null)
    {
        var store = new SqliteListingStore(DbPath, maxBytes, time);
        _stores.Add(store);
        return store;
    }

    private static FileEntry File(string name, long size = 1, FileEntryKind kind = FileEntryKind.File) =>
        new(name, Folder.Combine(name), kind, size, Stamp, Stamp.AddMinutes(size), FileAttributes.Archive);

    private static CachedListing Listing(params FileEntry[] entries) => new(entries, Stamp, FolderKind.Videos);

    [Fact]
    public void Constructing_DoesNoIo()
    {
        _ = Open();

        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task ASavedListing_ComesBackIdentical_MarkedAsFromDisk()
    {
        using var first = new SqliteListingStore(DbPath);
        var identity = new FileIdentity(1, 2, 3);
        var entries = new[]
        {
            File("a.mkv", 1_500_000_000),
            File("Ünïcode ✓ name.txt", 0),
            File("sub", 0, FileEntryKind.Folder) with { },
            new FileEntry("odd", LocationPath.Parse(@"C:\elsewhere\odd"), FileEntryKind.Link, 7, Stamp, Stamp, FileAttributes.Hidden | FileAttributes.System) { Identity = identity },
        };
        await first.SaveAsync(Key, Listing(entries), CancellationToken.None);

        var loaded = await Open().LoadAsync(Key, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(CacheSource.Disk, loaded.Source);
        Assert.Equal(Stamp, loaded.ListedAt);
        Assert.Equal(FolderKind.Videos, loaded.Kind);
        Assert.Equal(entries.Length, loaded.Entries.Count);
        for (var i = 0; i < entries.Length; i++)
        {
            Assert.Equal(entries[i], loaded.Entries[i]);
            Assert.Equal(entries[i].Identity, loaded.Entries[i].Identity);
            Assert.Equal(entries[i].Location.Value, loaded.Entries[i].Location.Value);
        }
    }

    [Fact]
    public async Task AnUnknownFolder_IsNull_AndHiddenAndSystemVariantsAreSeparate()
    {
        var store = Open();
        await store.SaveAsync(Key, Listing(File("a")), CancellationToken.None);

        Assert.Null(await store.LoadAsync(new ListingKey(Folder, true, false), CancellationToken.None));
        Assert.Null(await store.LoadAsync(new ListingKey(Folder.Combine("other"), false, false), CancellationToken.None));
        Assert.NotNull(await store.LoadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task SavingAgain_ReplacesTheListing_AndAnEmptyFolderIsRemembered()
    {
        var store = Open();
        await store.SaveAsync(Key, Listing(File("a"), File("b")), CancellationToken.None);
        await store.SaveAsync(Key, Listing(), CancellationToken.None);

        var loaded = await store.LoadAsync(Key, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public async Task Touch_ChangesTheTime_WithoutRewritingTheRows()
    {
        var store = Open();
        await store.SaveAsync(Key, Listing(File("a")), CancellationToken.None);
        var later = Stamp.AddDays(3);

        await store.TouchAsync(Key, later, CancellationToken.None);

        var loaded = await store.LoadAsync(Key, CancellationToken.None);
        Assert.Equal(later, loaded!.ListedAt);
        Assert.Single(loaded.Entries);
    }

    [Fact]
    public async Task Remove_ForgetsTheFolder_AndWithDescendantsEverythingBelow_ButNotSiblingsWithTheSamePrefix()
    {
        var store = Open();
        var parent = LocationPath.Parse(@"C:\media");
        var child = new ListingKey(parent.Combine("films"), false, false);
        var grandchild = new ListingKey(parent.Combine("films").Combine("2020"), false, false);
        var sibling = new ListingKey(LocationPath.Parse(@"C:\media-backup"), false, false);
        foreach (var key in new[] { new ListingKey(parent, false, false), child, grandchild, sibling })
        {
            await store.SaveAsync(key, Listing(), CancellationToken.None);
        }

        await store.RemoveAsync(parent, includeDescendants: false, CancellationToken.None);
        Assert.Null(await store.LoadAsync(new ListingKey(parent, false, false), CancellationToken.None));
        Assert.NotNull(await store.LoadAsync(child, CancellationToken.None));

        await store.RemoveAsync(parent, includeDescendants: true, CancellationToken.None);
        Assert.Null(await store.LoadAsync(child, CancellationToken.None));
        Assert.Null(await store.LoadAsync(grandchild, CancellationToken.None));
        Assert.NotNull(await store.LoadAsync(sibling, CancellationToken.None));
    }

    [Fact]
    public async Task FolderNames_AreMatchedWithoutRegardToCase()
    {
        var store = Open();
        await store.SaveAsync(Key, Listing(File("a")), CancellationToken.None);

        var loaded = await store.LoadAsync(new ListingKey(LocationPath.Parse(@"C:\MEDIA\Films"), false, false), CancellationToken.None);

        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task WhenTheCapIsExceeded_TheLeastRecentlyUsedFoldersGoFirst()
    {
        var time = new ManualTimeProvider();
        var names = Enumerable.Range(0, 3000).Select(i => $"file number {i} with a long enough name {Guid.NewGuid():N}").ToArray();
        CachedListing Big(ListingKey key) => Listing([.. names.Select((n, i) => new FileEntry(n, key.Location.Combine(n), FileEntryKind.File, i, Stamp, Stamp.AddMinutes(i), FileAttributes.Archive))]);
        var a = new ListingKey(Folder.Combine("a"), false, false);
        var b = new ListingKey(Folder.Combine("b"), false, false);
        var c = new ListingKey(Folder.Combine("c"), false, false);
        var probe = Open(time: time);
        await probe.SaveAsync(a, Big(a), CancellationToken.None);
        var (_, oneFolder) = await probe.UsageAsync();
        probe.Dispose();
        SqliteConnection.ClearAllPools();
        File_Delete(DbPath);

        // Room for two folders and a bit, not three.
        var store = Open(maxBytes: oneFolder * 2 + oneFolder / 2, time: time);
        await store.SaveAsync(a, Big(a), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await store.SaveAsync(b, Big(b), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));

        // Looking at "a" again makes "b" the least recently used one.
        Assert.NotNull(await store.LoadAsync(a, CancellationToken.None));
        await UntilAsync(async () => await store.LoadAsync(b, CancellationToken.None) is not null);
        await Task.Delay(100);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await store.LoadAsync(a, CancellationToken.None));
        await Task.Delay(100);
        time.Advance(TimeSpan.FromMinutes(1));

        await store.SaveAsync(c, Big(c), CancellationToken.None);

        Assert.Equal(2, (await store.UsageAsync()).Folders);
        Assert.NotNull(await store.LoadAsync(a, CancellationToken.None));
        Assert.Null(await store.LoadAsync(b, CancellationToken.None));
        Assert.NotNull(await store.LoadAsync(c, CancellationToken.None));
    }

    [Fact]
    public async Task ADamagedFile_IsReplacedByAnEmptyOne()
    {
        Directory.CreateDirectory(_folder);
        await System.IO.File.WriteAllTextAsync(DbPath, "this is not a database, just text that is long enough to look like a file header and then some");
        var store = Open();

        Assert.Null(await store.LoadAsync(Key, CancellationToken.None));

        await store.SaveAsync(Key, Listing(File("a")), CancellationToken.None);
        Assert.NotNull(await store.LoadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task ADamagedRow_IsReportedAsInvalidData_AndDroppedSoItIsListedAgain()
    {
        var store = Open();
        await store.SaveAsync(Key, Listing(File("a"), File("b")), CancellationToken.None);
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE listing SET data = x'0102030405';";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(Key, CancellationToken.None));
        await UntilAsync(async () => await store.LoadAsync(Key, CancellationToken.None) is null);
    }

    [Fact]
    public async Task AFileWithAnotherSchemaVersion_IsRecreated()
    {
        var store = Open();
        await store.SaveAsync(Key, Listing(File("a")), CancellationToken.None);
        store.Dispose();
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var reopened = Open();

        Assert.Null(await reopened.LoadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task ListingsNotRefreshedForTheRetentionPeriod_AreDroppedWhenTheFileIsOpened()
    {
        var time = new ManualTimeProvider(Stamp);
        var store = Open(time: time);
        await store.SaveAsync(Key, Listing(File("a")), CancellationToken.None);
        store.Dispose();
        SqliteConnection.ClearAllPools();

        time.Advance(SqliteListingStore.Retention + TimeSpan.FromDays(1));
        var reopened = Open(time: time);

        Assert.Null(await reopened.LoadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Clear_ForgetsEverything()
    {
        var store = Open();
        await store.SaveAsync(Key, Listing(File("a")), CancellationToken.None);

        await store.ClearAsync();

        Assert.Equal((0, 0L), await store.UsageAsync());
    }

    [Fact]
    public async Task ALargeFolder_IsSmallOnDisk_AndLoadsQuickly()
    {
        var store = Open();
        var entries = Enumerable.Range(0, 50_000).Select(i => File($"Clip {i:00000} {(i % 3 == 0 ? "a.mp4" : "b.txt")}", i * 1000L)).ToArray();

        await store.SaveAsync(Key, Listing(entries), CancellationToken.None);
        var (_, bytes) = await store.UsageAsync();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var loaded = await store.LoadAsync(Key, CancellationToken.None);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Assert.Equal(50_000, loaded!.Entries.Count);
        Assert.True(bytes < 5_000_000, $"{bytes} bytes for 50,000 entries");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"loading took {elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task TheCache_UsesTheStore_ForAFolderItHasNotSeenThisRun()
    {
        var store = Open();
        var first = new ListingCache(store: store);
        first.Put(Key, [File("a"), File("b")], Stamp, FolderKind.General);
        await UntilAsync(async () => (await store.UsageAsync()).Folders == 1);

        var second = new ListingCache(store: Open());
        var found = await second.TryGetAsync(Key, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(CacheSource.Disk, found.Source);
        Assert.Equal(2, found.Entries.Count);
    }

    private static void File_Delete(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            System.IO.File.Delete(file);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20);
        }
    }
}
