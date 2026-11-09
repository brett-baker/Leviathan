using Leviathan.Data;
using Leviathan.Interop;
using Leviathan.ViewModels.Tags;
using Leviathan.ViewModels.Triage;
using Microsoft.Data.Sqlite;

namespace Leviathan.Interop.Tests;

/// <summary>Tags on a real NTFS folder: the file ID is read, the stream is written, the modified time stays, and a rename keeps the tags (spec §11.2).</summary>
public sealed class TagStorageOnDiskTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-tagdisk-" + Guid.NewGuid().ToString("N"));

    public TagStorageOnDiskTests() => Directory.CreateDirectory(_folder);

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

    private IndexedTagStore Store(out SqliteTagIndex index)
    {
        var made = new SqliteTagIndex(new LeviathanDatabase(Path.Combine(_folder, "leviathan.db")));
        index = made;
        return new IndexedTagStore(new AlternateStreamTagStore(), new Lazy<ITagIndex>(made), new WindowsFileIdentity());
    }

    [Fact]
    public void Identity_OfARealFile_IsStable_AndSurvivesARename()
    {
        var path = Path.Combine(_folder, "a.txt");
        File.WriteAllText(path, "x");
        var identities = new WindowsFileIdentity();

        var before = identities.Get(path);
        var renamed = Path.Combine(_folder, "b.txt");
        File.Move(path, renamed);

        Assert.NotNull(before);
        Assert.Equal(before, identities.Get(renamed));
        Assert.Null(identities.Get(Path.Combine(_folder, "missing.txt")));
    }

    [Fact]
    public async Task Set_OnNtfs_WritesStreamAndIndex_KeepsModifiedTime_AndSurvivesARenameByAnotherProgram()
    {
        var store = Store(out var index);
        var path = Path.Combine(_folder, "photo.jpg");
        File.WriteAllText(path, "pixels");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        await store.SetAsync(path, new HashSet<int> { 4 }, CancellationToken.None);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal([path], index.PathsWithTag(4));

        var moved = Path.Combine(_folder, "renamed.jpg");
        File.Move(path, moved);
        var tags = await store.GetAsync(moved, CancellationToken.None);

        Assert.Equal([4], tags.ToArray());
        Assert.Equal([moved], index.PathsWithTag(4));
    }
}
