using Leviathan.Data;
using Leviathan.ViewModels.Activity;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteActivityStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-activity-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteActivityStore> _stores = [];
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Session = Guid.NewGuid();

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

    private string DbPath => Path.Combine(_folder, "activity.db");

    private SqliteActivityStore Open()
    {
        var store = new SqliteActivityStore(DbPath);
        _stores.Add(store);
        return store;
    }

    private static ActivityEntry Entry(string summary, TimeSpan ago, ActivityKind kind = ActivityKind.Files, string? target = null, string? location = null, bool error = false) =>
        new(0, Now - ago, Session, kind, "edit.delete", summary, target, location, error);

    [Fact]
    public void Constructing_DoesNoIo()
    {
        _ = Open();

        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task Entries_ComeBackNewestFirst_WithEveryField_AfterARestart()
    {
        var first = Open();
        await first.AppendAsync(
        [
            Entry("Deleted a.mp4", TimeSpan.FromMinutes(5), target: @"C:\a.mp4", location: @"C:\a.mp4"),
            Entry("Copy failed: ünïcode ✓", TimeSpan.FromMinutes(1), ActivityKind.Message, error: true),
        ], CancellationToken.None);

        var loaded = await Open().LoadAsync(Now.AddDays(-1), 100, CancellationToken.None);

        Assert.Equal(["Copy failed: ünïcode ✓", "Deleted a.mp4"], loaded.Select(e => e.Summary));
        Assert.True(loaded[0].IsError);
        Assert.Equal(ActivityKind.Message, loaded[0].Kind);
        Assert.Null(loaded[0].Target);
        Assert.Equal(@"C:\a.mp4", loaded[1].Target);
        Assert.Equal(@"C:\a.mp4", loaded[1].Location);
        Assert.Equal(Session, loaded[1].Session);
        Assert.Equal(Now - TimeSpan.FromMinutes(5), loaded[1].Time);
    }

    [Fact]
    public async Task Load_HonoursTheStartAndTheLimit()
    {
        var store = Open();
        await store.AppendAsync(
        [
            Entry("old", TimeSpan.FromDays(9)),
            Entry("a", TimeSpan.FromHours(3)),
            Entry("b", TimeSpan.FromHours(2)),
            Entry("c", TimeSpan.FromHours(1)),
        ], CancellationToken.None);

        var recent = await store.LoadAsync(Now.AddDays(-7), 2, CancellationToken.None);

        Assert.Equal(["c", "b"], recent.Select(e => e.Summary));
    }

    [Fact]
    public async Task Prune_ForgetsOlderEntries_AndClearForgetsAll()
    {
        var store = Open();
        await store.AppendAsync([Entry("old", TimeSpan.FromDays(9)), Entry("new", TimeSpan.FromHours(1))], CancellationToken.None);

        await store.PruneAsync(Now.AddDays(-7), CancellationToken.None);
        Assert.Equal(["new"], (await store.LoadAsync(Now.AddYears(-1), 100, CancellationToken.None)).Select(e => e.Summary));

        await store.ClearAsync(CancellationToken.None);
        Assert.Empty(await store.LoadAsync(Now.AddYears(-1), 100, CancellationToken.None));
    }

    [Fact]
    public async Task ADamagedFile_IsReplacedByAnEmptyHistory()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(DbPath, "this is not a database, just text that is long enough to look like a file header and then some");
        var store = Open();

        Assert.Empty(await store.LoadAsync(Now.AddYears(-1), 100, CancellationToken.None));

        await store.AppendAsync([Entry("after", TimeSpan.Zero)], CancellationToken.None);
        Assert.Single(await store.LoadAsync(Now.AddYears(-1), 100, CancellationToken.None));
    }

    [Fact]
    public async Task AFileWithAnotherSchemaVersion_IsRecreated()
    {
        var store = Open();
        await store.AppendAsync([Entry("x", TimeSpan.Zero)], CancellationToken.None);
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

        Assert.Empty(await Open().LoadAsync(Now.AddYears(-1), 100, CancellationToken.None));
    }

    [Fact]
    public async Task TheLog_WritesToTheStore_AndANewRunSeesTheEarlierSession()
    {
        var store = Open();
        var first = new ActivityLog(store);
        first.Add(new Leviathan.Diagnostics.UserAction(DateTimeOffset.UtcNow, "edit.rename", @"C:\a.txt"));
        first.Dispose();

        var second = new ActivityLog(Open());
        await second.LoadEarlierAsync();

        Assert.Equal("Renamed a.txt", Assert.Single(second.Find(new ActivityQuery(IncludeEarlier: true))).Summary);
        Assert.Empty(second.Find());
    }
}
