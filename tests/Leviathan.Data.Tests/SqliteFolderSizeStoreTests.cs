using Leviathan.Core.Paths;
using Leviathan.Data;
using Leviathan.Engine.FolderSizes;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteFolderSizeStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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

    private LeviathanDatabase CreateDatabase() => new(Path.Combine(_folder, "leviathan.db"));

    private static FolderSize Size(string path, long total, FolderSizeState state = FolderSizeState.Fresh, DateTimeOffset? modified = null) =>
        new(LocationPath.Parse(path), total / 2, 1, total, 2, 1, state, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), modified);

    [Fact]
    public void Migrations_RunOnce_AndRecordVersion()
    {
        var db = CreateDatabase();
        var again = CreateDatabase();

        Assert.Equal(LeviathanDatabase.Migrations.Count, db.SchemaVersion());
        Assert.Equal(LeviathanDatabase.Migrations.Count, again.SchemaVersion());
    }

    [Fact]
    public void Upsert_ThenGet_RoundTripsEveryField()
    {
        var store = new SqliteFolderSizeStore(CreateDatabase());
        var modified = new DateTimeOffset(2026, 10, 31, 22, 10, 0, TimeSpan.Zero);
        var original = Size(@"\\nas\media\inbox", 12_800_000_000_000, FolderSizeState.Partial, modified);

        store.Upsert(original);
        var read = store.Get(LocationPath.Parse(@"\\NAS\Media\Inbox"));

        Assert.NotNull(read);
        Assert.Equal(original with { Location = read!.Location }, read);
    }

    [Fact]
    public void Upsert_Replaces_ExistingRow()
    {
        var store = new SqliteFolderSizeStore(CreateDatabase());
        store.Upsert(Size(@"C:\a", 10));

        store.Upsert(Size(@"C:\a", 20, FolderSizeState.Stale));

        var read = store.Get(LocationPath.Parse(@"C:\a"))!;
        Assert.Equal(20, read.TotalBytes);
        Assert.Equal(FolderSizeState.Stale, read.State);
        Assert.Null(read.FolderModified);
    }

    [Fact]
    public void RemoveTree_RemovesFolderAndDescendants_NotSiblingsWithSamePrefix()
    {
        var store = new SqliteFolderSizeStore(CreateDatabase());
        foreach (var path in new[] { @"C:\a", @"C:\a\b", @"C:\a\b\c", @"C:\a\b_x", @"C:\a\bb" })
        {
            store.Upsert(Size(path, 1));
        }

        store.RemoveTree(LocationPath.Parse(@"C:\a\b"));

        Assert.NotNull(store.Get(LocationPath.Parse(@"C:\a")));
        Assert.Null(store.Get(LocationPath.Parse(@"C:\a\b")));
        Assert.Null(store.Get(LocationPath.Parse(@"C:\a\b\c")));
        Assert.NotNull(store.Get(LocationPath.Parse(@"C:\a\b_x")));
        Assert.NotNull(store.Get(LocationPath.Parse(@"C:\a\bb")));
    }

    [Fact]
    public async Task ConcurrentWriters_DoNotFail()
    {
        var store = new SqliteFolderSizeStore(CreateDatabase());

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                store.Upsert(Size($@"C:\w{worker}\f{i}", i));
            }
        })));

        Assert.Equal(49, store.Get(LocationPath.Parse(@"C:\w7\f49"))!.TotalBytes);
    }

    [Fact]
    public void OwnFolders_RoundTrips_AndNullStaysNull()
    {
        var store = new SqliteFolderSizeStore(CreateDatabase());
        store.Upsert(Size(@"C:\with", 10) with { OwnFolders = 4 });
        store.Upsert(Size(@"C:\without", 10));

        Assert.Equal(4, store.Get(LocationPath.Parse(@"C:\with"))!.OwnFolders);
        Assert.Null(store.Get(LocationPath.Parse(@"C:\without"))!.OwnFolders);
    }

    [Fact]
    public void ADatabaseFromBeforeOwnFolders_IsUpgraded_AndItsRowsReadAsUnknown()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "leviathan.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = string.Join(Environment.NewLine, LeviathanDatabase.Migrations.Take(3)) + """

                INSERT INTO folder_size VALUES ('C:\old', 1, 1, 2, 2, 1, 0, '2026-11-01T12:00:00.0000000+00:00', NULL);
                CREATE TABLE schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_version VALUES (1, 'x'), (2, 'x'), (3, 'x');
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var store = new SqliteFolderSizeStore(new LeviathanDatabase(path));

        var old = store.Get(LocationPath.Parse(@"C:\old"))!;
        Assert.Equal(2, old.TotalBytes);
        Assert.Null(old.OwnFolders);
    }
}
