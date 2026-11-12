using Leviathan.Data;
using Leviathan.Media;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

/// <summary>Perceptual signatures kept between runs, by file version (spec §15.2).</summary>
public sealed class SqliteMediaSignatureStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-signature-db-" + Guid.NewGuid().ToString("N"));

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

    private SqliteMediaSignatureStore Store() => new(new LeviathanDatabase(Path.Combine(_folder, "leviathan.db")));

    private static MediaFileKey Key(string path = @"D:\p\a.jpg", long size = 1000) => new(path, size, new DateTime(2026, 5, 1, 8, 30, 15, DateTimeKind.Utc).AddTicks(4567));

    [Fact]
    public void Put_ThenGet_RoundTripsEveryBitOfEveryHash()
    {
        var store = Store();
        var signature = new MediaSignature(SignatureKind.Video, [0UL, ulong.MaxValue, 0x8000000000000001UL, 42UL, 0xDEADBEEFCAFEF00DUL], 3600.25);

        store.Put(Key(), signature);

        var read = store.Get(Key());
        Assert.NotNull(read);
        Assert.Equal(SignatureKind.Video, read.Kind);
        Assert.Equal(signature.Hashes, read.Hashes);
        Assert.Equal(3600.25, read.DurationSeconds);
    }

    [Fact]
    public void Get_ForAChangedFileOrAnUnknownPath_IsNull_AndPathsCompareWithoutCase()
    {
        var store = Store();
        store.Put(Key(), new MediaSignature(SignatureKind.Image, [7UL], 0));

        Assert.Null(store.Get(Key(size: 1001)));
        Assert.Null(store.Get(Key(@"D:\p\other.jpg")));
        Assert.NotNull(store.Get(Key(@"d:\P\A.JPG")));
    }

    [Fact]
    public void Put_ReplacesTheSignatureOfTheSamePath()
    {
        var store = Store();
        store.Put(Key(), new MediaSignature(SignatureKind.Image, [1UL], 0));
        store.Put(Key(size: 2000), new MediaSignature(SignatureKind.Image, [2UL], 0));

        Assert.Null(store.Get(Key()));
        Assert.Equal([2UL], store.Get(Key(size: 2000))!.Hashes);
    }

    [Fact]
    public void ASecondStoreOverTheSameFile_SeesWhatTheFirstKept()
    {
        Store().Put(Key(), new MediaSignature(SignatureKind.Image, [99UL], 0));

        Assert.Equal([99UL], Store().Get(Key())!.Hashes);
    }
}
