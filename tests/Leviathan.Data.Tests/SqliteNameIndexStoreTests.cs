using Leviathan.Core.Paths;
using Leviathan.Search;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteNameIndexStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-index-db-" + Guid.NewGuid().ToString("N"));

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

    private SqliteNameIndexStore Store() => new(new LeviathanDatabase(Path.Combine(_folder, "leviathan.db")));

    private static NameIndex Sample()
    {
        var index = new NameIndex();
        var root = index.AddRoot(LocationPath.Parse(@"\\nas\media"));
        var ids = index.ReplaceChildren(root, [new IndexedChild("Ünïcödé ✓", true, 0, 100, FileAttributes.Directory), new IndexedChild("a.mkv", false, 5_000_000_000, 638_000_000_000_000_000L, FileAttributes.ReadOnly)]);
        index.ReplaceChildren(ids[0], [new IndexedChild("deep.txt", false, 1, 2, FileAttributes.Normal)]);
        index.MarkCrawled(root, new DateTimeOffset(2026, 11, 10, 8, 30, 15, TimeSpan.Zero));
        return index;
    }

    [Fact]
    public void Load_WithNothingSaved_IsNull() => Assert.Null(Store().Load());

    [Fact]
    public void SaveThenLoad_GivesTheSameIndex_IncludingSizesTimesAndCoverage()
    {
        using var original = Sample();
        Store().Save(original.Export());

        using var restored = new NameIndex();
        restored.Import(Store().Load()!);

        Assert.Equal(original.Count, restored.Count);
        var entry = Assert.Single(restored.Candidates("a.mkv", [], null, 10, CancellationToken.None));
        Assert.Equal(5_000_000_000, entry.Size);
        Assert.Equal(638_000_000_000_000_000L, entry.Modified.UtcTicks);
        Assert.True(entry.Attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.Single(restored.Candidates("deep.txt", [], null, 10, CancellationToken.None));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 30, 15, TimeSpan.Zero), restored.CoveredSince(LocationPath.Parse(@"\\nas\media\Ünïcödé ✓")));
    }

    [Fact]
    public void Save_ReplacesThePreviousCopy()
    {
        using var first = Sample();
        var store = Store();
        store.Save(first.Export());
        using var second = new NameIndex();
        second.ReplaceChildren(second.AddRoot(LocationPath.Parse(@"D:\")), [new IndexedChild("only.txt", false, 1, 1, FileAttributes.Normal)]);

        store.Save(second.Export());

        using var restored = new NameIndex();
        restored.Import(store.Load()!);
        Assert.Equal(1, restored.Count);
        Assert.Empty(restored.Candidates("a.mkv", [], null, 10, CancellationToken.None));
    }

    [Fact]
    public void Save_AMillionRows_IsQuickEnoughToDoAfterACrawl()
    {
        using var index = new NameIndex();
        var root = index.AddRoot(LocationPath.Parse(@"D:\"));
        for (var f = 0; f < 200; f++)
        {
            var folder = index.ReplaceChildren(root, [.. index.ChildrenOf(root).Select(index.Describe), new IndexedChild("f" + f, true, 0, 1, FileAttributes.Directory)])[^1];
            index.ReplaceChildren(folder, [.. Enumerable.Range(0, 500).Select(i => new IndexedChild($"file_{f}_{i}.txt", false, i, 1, FileAttributes.Normal))]);
        }

        var store = Store();
        var started = System.Diagnostics.Stopwatch.StartNew();
        store.Save(index.Export());
        var saved = started.Elapsed;
        started.Restart();
        var loaded = store.Load();

        Assert.Equal(100_201, loaded!.Rows.Count);
        Assert.True(saved < TimeSpan.FromSeconds(10), $"saving took {saved}");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"loading took {started.Elapsed}");
    }
}
