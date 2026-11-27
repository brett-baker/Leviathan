using Leviathan.Data;
using Leviathan.ViewModels.Triage.Learning;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteFileFactStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-facts-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteFileFactStore> _stores = [];
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

    private string DbPath => Path.Combine(_folder, "triage-facts.db");

    private SqliteFileFactStore Open()
    {
        var store = new SqliteFileFactStore(DbPath);
        _stores.Add(store);
        return store;
    }

    private static FileFacts Facts(string path, bool complete = true) => new(
        path,
        1234,
        5678,
        Now,
        new NameFacts(["cadence", "lux"], "Working Folder", ".mp4", 2019, 1080),
        new VideoFacts(900.5, 1920, 1080, "h264", 5_000_000),
        [new SceneFacts("s1", "Title", "Studio", "2019-02-03", 70, ["Jane"], ["feet"])],
        ["s1"],
        complete);

    [Fact]
    public async Task Get_OfAFileNeverSaved_IsNull()
    {
        Assert.Null(await Open().GetAsync(@"Z:\a.mp4", CancellationToken.None));
    }

    [Fact]
    public async Task Save_ThenGet_ReturnsEveryFact()
    {
        var store = Open();
        var facts = Facts(@"Z:\a.mp4");

        await store.SaveAsync(facts, CancellationToken.None);
        var loaded = await store.GetAsync(@"Z:\a.mp4", CancellationToken.None);

        Assert.Equal(facts.Name.Tokens, loaded!.Name.Tokens);
        Assert.Equal(2019, loaded.Name.Year);
        Assert.Equal(facts.Video, loaded.Video);
        Assert.Equal(facts.Scenes[0].Performers, loaded.Scenes[0].Performers);
        Assert.Equal(["s1"], loaded.Asked);
        Assert.Equal(Now, loaded.Collected);
    }

    [Fact]
    public async Task Save_AgainForTheSamePath_ReplacesIt_WhateverTheCase()
    {
        var store = Open();
        await store.SaveAsync(Facts(@"Z:\A.mp4", complete: false), CancellationToken.None);

        await store.SaveAsync(Facts(@"z:\a.MP4"), CancellationToken.None);

        Assert.Single(await store.LoadAllAsync(CancellationToken.None));
        Assert.Equal((1, 0), await store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Count_SplitsCompleteFromWaiting()
    {
        var store = Open();
        await store.SaveAsync(Facts(@"Z:\a.mp4"), CancellationToken.None);
        await store.SaveAsync(Facts(@"Z:\b.mp4", complete: false), CancellationToken.None);
        await store.SaveAsync(Facts(@"Z:\c.mp4", complete: false), CancellationToken.None);

        Assert.Equal((1, 2), await store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Count_OfANewStore_IsZero()
    {
        Assert.Equal((0, 0), await Open().CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Facts_SurviveARestart()
    {
        await Open().SaveAsync(Facts(@"Z:\a.mp4"), CancellationToken.None);
        SqliteConnection.ClearAllPools();

        Assert.NotNull(await Open().GetAsync(@"Z:\a.mp4", CancellationToken.None));
    }

    [Fact]
    public async Task ADamagedFile_IsStartedOver_BecauseTheFactsCanBeCollectedAgain()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(DbPath, "this is not a database " + new string('x', 400));
        var store = Open();

        await store.SaveAsync(Facts(@"Z:\a.mp4"), CancellationToken.None);

        Assert.Single(await store.LoadAllAsync(CancellationToken.None));
    }
}
