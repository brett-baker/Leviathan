using Leviathan.Data;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Triage.Learning;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteTriageHistoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-triage-history-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteTriageHistory> _stores = [];
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

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

    private string DbPath => Path.Combine(_folder, "triage-history.db");

    private SqliteTriageHistory Open()
    {
        var store = new SqliteTriageHistory(DbPath);
        _stores.Add(store);
        return store;
    }

    private static TriageDecisionRecord Rec(string path, TriageDecision decision, int minute, string? reason = null) =>
        new(Now.AddMinutes(minute), path, decision, reason, 1234, 5678);

    [Fact]
    public async Task Load_OfANewFile_IsEmpty()
    {
        Assert.Empty(await Open().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Append_ThenLoad_ReturnsEveryDecisionInOrder_WithAllFields()
    {
        var store = Open();
        await store.AppendAsync(Rec(@"Z:\a.mp4", TriageDecision.Discarded, 0, "short"), CancellationToken.None);
        await store.AppendAsync(Rec(@"Z:\b.mp4", TriageDecision.Kept, 1), CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal([Rec(@"Z:\a.mp4", TriageDecision.Discarded, 0, "short"), Rec(@"Z:\b.mp4", TriageDecision.Kept, 1)], loaded);
    }

    [Fact]
    public async Task Decisions_SurviveARestart()
    {
        await Open().AppendAsync(Rec(@"Z:\a.mp4", TriageDecision.Kept, 0), CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var loaded = await Open().LoadAsync(CancellationToken.None);

        Assert.Equal(@"Z:\a.mp4", Assert.Single(loaded).Path);
    }

    [Fact]
    public async Task APathWithOddCharacters_IsStoredAsItIs()
    {
        var path = @"\\nas\share\Rén'ée ""x"" 日本語\a;b.mp4";
        var store = Open();
        await store.AppendAsync(Rec(path, TriageDecision.Kept, 0), CancellationToken.None);

        Assert.Equal(path, Assert.Single(await store.LoadAsync(CancellationToken.None)).Path);
    }

    [Fact]
    public async Task ADamagedFile_IsMovedAside_NotDeleted_AndANewOneStarts()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(DbPath, "this is not a database, and it is long enough to be mistaken for one at a glance " + new string('x', 300));
        var store = Open();

        await store.AppendAsync(Rec(@"Z:\a.mp4", TriageDecision.Kept, 0), CancellationToken.None);

        Assert.Single(await store.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(_folder, "triage-history.db.damaged-*"));
    }
}
