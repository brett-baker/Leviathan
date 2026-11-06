using Leviathan.Data;
using Leviathan.Media;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data.Tests;

public sealed class SqliteMediaMetadataStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-media-db-" + Guid.NewGuid().ToString("N"));

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

    private SqliteMediaMetadataStore Store() => new(new LeviathanDatabase(Path.Combine(_folder, "leviathan.db")));

    private static MediaFileKey Key(string path = @"D:\v\a.mkv", long size = 1000, int day = 1) => new(path, size, new DateTime(2026, 5, day, 8, 30, 15, 123, DateTimeKind.Utc).AddTicks(4567));

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static MediaInfo Info() => FfprobeParser.TryParse("""
        {"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"avg_frame_rate":"30000/1001","color_transfer":"smpte2084"},
                    {"index":1,"codec_type":"audio","codec_name":"eac3","channels":6,"tags":{"language":"eng"}}],
         "chapters":[{"start_time":"0","end_time":"10","tags":{"title":"One"}}],
         "format":{"format_name":"matroska,webm","duration":"3600.25","bit_rate":"25000000","tags":{"title":"Movie"}}}
        """)!;

    [Fact]
    public void Put_ThenGet_RoundTripsTheParsedMetadata()
    {
        var store = Store();
        var info = Info();

        store.Put(new MediaMetadataEntry(Key(), MetadataState.Ok, info, null, Now));
        var back = store.Get(Key());

        Assert.NotNull(back);
        Assert.Equal(MetadataState.Ok, back.State);
        Assert.Equal(info.Container, back.Info!.Container);
        Assert.Equal(info.Duration, back.Info.Duration);
        Assert.Equal(HdrKind.Hdr10, back.Info.PrimaryVideo!.Hdr);
        Assert.Equal(29.97, back.Info.PrimaryVideo.FrameRate!.Value, 2);
        Assert.Equal(["eng"], back.Info.AudioLanguages);
        Assert.Equal("One", back.Info.Chapters[0].Title);
        Assert.Equal("Movie", back.Info.Tags["title"]);
        Assert.Equal(Now, back.At);
    }

    [Fact]
    public void Get_ForAnotherVersionOfTheFile_ReturnsNothing()
    {
        var store = Store();
        store.Put(new MediaMetadataEntry(Key(), MetadataState.Ok, Info(), null, Now));

        Assert.Null(store.Get(Key(size: 1001)));
        Assert.Null(store.Get(Key(day: 2)));
        Assert.NotNull(store.Get(Key()));
    }

    [Fact]
    public void Get_ComparesThePathWithoutRegardToCase()
    {
        var store = Store();
        store.Put(new MediaMetadataEntry(Key(@"D:\Videos\Clip.mkv"), MetadataState.Ok, Info(), null, Now));

        Assert.NotNull(store.Get(Key(@"d:\videos\clip.MKV")));
    }

    [Fact]
    public void Put_ReplacesTheEarlierAnswerForThePath()
    {
        var store = Store();
        store.Put(new MediaMetadataEntry(Key(size: 1), MetadataState.Unreadable, null, "bad", Now));

        store.Put(new MediaMetadataEntry(Key(size: 2), MetadataState.Ok, Info(), null, Now));

        Assert.Null(store.Get(Key(size: 1)));
        Assert.Equal(MetadataState.Ok, store.Get(Key(size: 2))!.State);
        Assert.Empty(store.Unreadable());
    }

    [Fact]
    public void Unreadable_KeepsItsReason_AndSurvivesAReopen()
    {
        Store().Put(new MediaMetadataEntry(Key(), MetadataState.Unreadable, null, "moov atom not found", Now));

        var reopened = Store();
        var entry = reopened.Get(Key());

        Assert.Equal(MetadataState.Unreadable, entry!.State);
        Assert.Equal("moov atom not found", entry.Message);
        Assert.Single(reopened.Unreadable());
    }

    [Fact]
    public void AProbeThatNeverFinished_BecomesUnreadable_OnRecovery_AndOnlyThat()
    {
        var store = Store();
        store.MarkProbing(Key(@"D:\v\crashed.mkv"), Now);
        store.Put(new MediaMetadataEntry(Key(@"D:\v\fine.mkv"), MetadataState.Ok, Info(), null, Now));

        var count = store.QuarantineInterrupted(Now.AddMinutes(1), "The media process stopped while reading this file.");

        Assert.Equal(1, count);
        var crashed = store.Get(Key(@"D:\v\crashed.mkv"))!;
        Assert.Equal(MetadataState.Unreadable, crashed.State);
        Assert.Equal("The media process stopped while reading this file.", crashed.Message);
        Assert.Equal(MetadataState.Ok, store.Get(Key(@"D:\v\fine.mkv"))!.State);
        Assert.Equal(0, store.QuarantineInterrupted(Now, "again"));
    }

    [Fact]
    public void Forget_RemovesTheAnswer_AndSaysWhetherThereWasOne()
    {
        var store = Store();
        store.Put(new MediaMetadataEntry(Key(), MetadataState.Ok, Info(), null, Now));

        Assert.True(store.Forget(@"D:\v\a.mkv"));
        Assert.False(store.Forget(@"D:\v\a.mkv"));
        Assert.Null(store.Get(Key()));
    }

    [Fact]
    public void Unreadable_ListsTheNewestFirst()
    {
        var store = Store();
        store.Put(new MediaMetadataEntry(Key(@"D:\v\old.mkv"), MetadataState.Unreadable, null, "a", Now));
        store.Put(new MediaMetadataEntry(Key(@"D:\v\new.mkv"), MetadataState.Unreadable, null, "b", Now.AddHours(1)));

        Assert.Equal([@"D:\v\new.mkv", @"D:\v\old.mkv"], store.Unreadable().Select(e => e.Key.Path));
    }

    [Fact]
    public void Get_OfARowThatCannotBeReadBack_IsAMiss_NotAnError()
    {
        var database = new LeviathanDatabase(Path.Combine(_folder, "leviathan.db"));
        var store = new SqliteMediaMetadataStore(database);
        store.Put(new MediaMetadataEntry(Key(), MetadataState.Ok, Info(), null, Now));
        using (var connection = database.Open())
        {
            using var damage = connection.CreateCommand();
            damage.CommandText = "UPDATE media_metadata SET json = '{not json';";
            damage.ExecuteNonQuery();
        }

        Assert.Null(store.Get(Key()));
    }
}
