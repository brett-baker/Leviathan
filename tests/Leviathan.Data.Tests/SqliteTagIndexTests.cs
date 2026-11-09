using Leviathan.Core.Model;
using Leviathan.Data;
using Leviathan.ViewModels.Tags;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteTagIndexTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-tags-" + Guid.NewGuid().ToString("N"));

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

    private SqliteTagIndex Make() => new(new LeviathanDatabase(Path.Combine(_folder, "leviathan.db")));

    private static TagIndexEntry Entry(string path, FileIdentity? id, params int[] tags) =>
        new(path, id, 10, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), new HashSet<int>(tags));

    [Fact]
    public void Put_ThenFind_RoundTripsByPathAndByIdentity_WithHighBitsSet()
    {
        var index = Make();
        var id = new FileIdentity(0xF000_0000_0000_0001UL, ulong.MaxValue, 0x8000_0000_0000_0002UL);
        index.Put(Entry(@"C:\a\X.jpg", id, 4, 1));

        var byPath = index.FindByPath(@"c:\a\x.JPG");
        var byId = index.FindByIdentity(id);

        Assert.NotNull(byPath);
        Assert.Equal(id, byPath.Identity);
        Assert.Equal([1, 4], byPath.Tags.Order().ToArray());
        Assert.Equal(10, byPath.Size);
        Assert.Equal(byPath.Path, byId?.Path);
    }

    [Fact]
    public void Put_ForAMovedFile_ReplacesTheRowOfItsOldPath()
    {
        var index = Make();
        var id = new FileIdentity(1, 2, 3);
        index.Put(Entry(@"C:\old\x.jpg", id, 1));

        index.Put(Entry(@"C:\new\x.jpg", id, 1));

        Assert.Null(index.FindByPath(@"C:\old\x.jpg"));
        Assert.NotNull(index.FindByPath(@"C:\new\x.jpg"));
    }

    [Fact]
    public void Put_WithNoTags_RemovesTheRow()
    {
        var index = Make();
        index.Put(Entry(@"E:\x.mp4", null, 2));

        index.Put(Entry(@"E:\x.mp4", null));

        Assert.Null(index.FindByPath(@"E:\x.mp4"));
    }

    [Fact]
    public void PathsWithTag_FindsWholeIdsOnly()
    {
        var index = Make();
        index.Put(Entry(@"C:\a.jpg", null, 1));
        index.Put(Entry(@"C:\b.jpg", null, 11, 2));
        index.Put(Entry(@"C:\c.jpg", null, 1, 2));

        Assert.Equal([@"C:\a.jpg", @"C:\c.jpg"], index.PathsWithTag(1));
        Assert.Equal([@"C:\b.jpg", @"C:\c.jpg"], index.PathsWithTag(2));
        Assert.Equal([@"C:\b.jpg"], index.PathsWithTag(11));
    }

    [Fact]
    public void Rows_SurviveReopeningTheDatabase()
    {
        Make().Put(Entry(@"C:\a.jpg", null, 3));

        Assert.Equal([3], Make().FindByPath(@"C:\a.jpg")!.Tags.ToArray());
    }
}
