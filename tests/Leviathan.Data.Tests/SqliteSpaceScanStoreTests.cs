using Leviathan.SpaceMap;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteSpaceScanStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-scan-db-" + Guid.NewGuid().ToString("N"));

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

    private SqliteSpaceScanStore Store() => new(new LeviathanDatabase(Path.Combine(_folder, "leviathan.db")));

    private static SpaceScanSnapshot Snapshot(string root, bool complete, int files) =>
        new(root, new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), complete,
        [
            new SpaceScanRow(-1, root, true, 0, 0, complete, false),
            .. Enumerable.Range(0, files).Select(i => new SpaceScanRow(0, $"f{i}.bin", false, 1000 + i, 638_000_000_000_000_000L + i, false, false)),
        ]);

    [Fact]
    public void Load_WithNothingSaved_IsNull() => Assert.Null(Store().Load(@"\\nas\media"));

    [Fact]
    public void SaveThenLoad_RoundTrips_AndTheRootIsNotCaseSensitive()
    {
        var store = Store();
        store.Save(Snapshot(@"\\nas\media", true, 50));

        var back = store.Load(@"\\NAS\Media")!;

        Assert.Equal(51, back.Rows.Count);
        Assert.True(back.IsComplete);
        Assert.Equal(1049, back.Rows[^1].OwnSize);
    }

    [Fact]
    public void Save_ReplacesThePreviousScanOfTheSameRoot_AndKeepsOthers()
    {
        var store = Store();
        store.Save(Snapshot(@"D:\a", false, 10));
        store.Save(Snapshot(@"E:\b", true, 5));

        store.Save(Snapshot(@"D:\a", true, 20));

        Assert.Equal(21, store.Load(@"D:\a")!.Rows.Count);
        Assert.True(store.Load(@"D:\a")!.IsComplete);
        Assert.Equal(6, store.Load(@"E:\b")!.Rows.Count);
    }

    [Fact]
    public void Delete_ForgetsOnlyThatRoot()
    {
        var store = Store();
        store.Save(Snapshot(@"D:\a", true, 1));
        store.Save(Snapshot(@"E:\b", true, 1));

        store.Delete(@"D:\a");

        Assert.Null(store.Load(@"D:\a"));
        Assert.NotNull(store.Load(@"E:\b"));
    }

    [Fact]
    public void Save_AMillionFileScan_IsSmallAndQuick()
    {
        // Parents come first: the root, then the 1000 folders, then the files of each folder.
        var rows = new List<SpaceScanRow> { new(-1, @"D:\", true, 0, 0, true, false) };
        rows.AddRange(Enumerable.Range(0, 1000).Select(f => new SpaceScanRow(0, "folder" + f, true, 0, 0, true, false)));
        var folderCount = rows.Count - 1;
        for (var f = 0; f < 1000; f++)
        {
            for (var i = 0; i < 1000; i++)
            {
                rows.Add(new SpaceScanRow(1 + f, $"file_{f}_{i}.dat", false, i * 37L, 638_000_000_000_000_000L + i, false, false));
            }
        }

        var store = Store();
        var started = System.Diagnostics.Stopwatch.StartNew();
        store.Save(new SpaceScanSnapshot(@"D:\", DateTimeOffset.UnixEpoch, true, rows));
        var saved = started.Elapsed;
        started.Restart();
        var back = store.Load(@"D:\");

        Assert.Equal(1_001_001, back!.Rows.Count);
        Assert.Equal(1000, folderCount);
        Assert.True(saved < TimeSpan.FromSeconds(10), $"saving took {saved}");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"loading took {started.Elapsed}");
    }
}
