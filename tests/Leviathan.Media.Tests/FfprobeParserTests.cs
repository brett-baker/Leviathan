namespace Leviathan.Media.Tests;

public sealed class FfprobeParserTests
{
    [Theory]
    [InlineData("plain.mp4.json")]
    [InlineData("multi.mkv.json")]
    [InlineData("hdr10.mkv.json")]
    [InlineData("hlg.mkv.json")]
    [InlineData("cover.mp3.json")]
    [InlineData("tone.flac.json")]
    public void Parse_OfRealFfprobeOutput_MatchesTheReviewedSnapshot(string sample)
    {
        var info = FfprobeParser.TryParse(Snapshot.Read(sample));

        Assert.NotNull(info);
        Snapshot.Verify(sample, info);
    }

    [Fact]
    public void Parse_OfThePlainClip_ReadsTheBasics()
    {
        var info = FfprobeParser.TryParse(Snapshot.Read("plain.mp4.json"))!;

        Assert.Equal("mov", info.Container);
        Assert.Equal(128, info.PrimaryVideo!.Width);
        Assert.Equal(72, info.PrimaryVideo.Height);
        Assert.Equal(24, info.PrimaryVideo.FrameRate);
        Assert.Equal("h264", info.PrimaryVideo.Codec);
        Assert.Equal(HdrKind.None, info.PrimaryVideo.Hdr);
        Assert.Equal("aac", info.PrimaryAudio!.Codec);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 1.9, 2.2);
    }

    [Fact]
    public void Parse_OfTheMatroskaFile_ReadsLanguagesSubtitlesAndChapters()
    {
        var info = FfprobeParser.TryParse(Snapshot.Read("multi.mkv.json"))!;

        Assert.Equal("matroska", info.Container);
        Assert.Equal(["eng", "jpn"], info.AudioLanguages);
        Assert.Equal(["eng", "jpn"], info.SubtitleLanguages);
        Assert.Contains(info.Subtitles, s => s.IsForced);
        Assert.Equal(["Opening", "Ending"], info.Chapters.Select(c => c.Title));
        Assert.Equal(TimeSpan.FromSeconds(1), info.Chapters[1].Start);
        Assert.Equal("Sample movie", info.Tags["title"]);
    }

    [Fact]
    public void Parse_SeesPqAsHdr10_AndHlgAsHlg()
    {
        Assert.Equal(HdrKind.Hdr10, FfprobeParser.TryParse(Snapshot.Read("hdr10.mkv.json"))!.PrimaryVideo!.Hdr);
        Assert.Equal(HdrKind.Hlg, FfprobeParser.TryParse(Snapshot.Read("hlg.mkv.json"))!.PrimaryVideo!.Hdr);
        Assert.Equal(10, FfprobeParser.TryParse(Snapshot.Read("hdr10.mkv.json"))!.PrimaryVideo!.BitDepth);
    }

    [Fact]
    public void Parse_OfCoverArt_DoesNotCountItAsVideo()
    {
        var info = FfprobeParser.TryParse(Snapshot.Read("cover.mp3.json"))!;

        Assert.Single(info.Video);
        Assert.True(info.Video[0].IsCoverArt);
        Assert.False(info.HasVideo);
        Assert.Null(info.PrimaryVideo);

        // ffprobe reports a picture's "frame rate" as 90000 (its time base); that is not a frame rate.
        Assert.Null(info.Video[0].FrameRate);
        Assert.Equal("Tone", info.Tags["title"]);
    }

    [Fact]
    public void Parse_OfAudioOnly_HasNoVideo()
    {
        var info = FfprobeParser.TryParse(Snapshot.Read("tone.flac.json"))!;

        Assert.False(info.HasVideo);
        Assert.Equal("flac", info.Audio[0].Codec);
        Assert.Equal(1, info.PrimaryAudio!.Channels);
    }

    [Fact]
    public void Parse_SeesADolbyVisionRecord_EvenWithoutAPqTag()
    {
        const string json = """{"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":8}]}],"format":{}}""";

        Assert.Equal(HdrKind.DolbyVision, FfprobeParser.TryParse(json)!.PrimaryVideo!.Hdr);
    }

    [Theory]
    [InlineData("30000/1001", 29.97)]
    [InlineData("25/1", 25.0)]
    [InlineData("0/0", null)]
    [InlineData("0/1", null)]
    [InlineData("garbage", null)]
    public void Parse_ReadsFrameRates_AndNeverInventsOne(string rate, double? expected)
    {
        var json = $$$"""{"streams":[{"index":0,"codec_type":"video","avg_frame_rate":"{{{rate}}}"}],"format":{}}""";

        var actual = FfprobeParser.TryParse(json)!.Video[0].FrameRate;

        Assert.Equal(expected is null, actual is null);
        if (expected is { } want)
        {
            Assert.Equal(want, actual!.Value, 2);
        }
    }

    [Fact]
    public void Parse_WhenTheAverageRateIsUnknown_FallsBackToTheBaseRate()
    {
        const string json = """{"streams":[{"index":0,"codec_type":"video","avg_frame_rate":"0/0","r_frame_rate":"50/1"}],"format":{}}""";

        Assert.Equal(50.0, FfprobeParser.TryParse(json)!.Video[0].FrameRate);
    }

    [Fact]
    public void Parse_TakesTheDurationFromTheStreams_WhenTheContainerHasNone()
    {
        const string json = """{"streams":[{"index":0,"codec_type":"audio","duration":"12.5"},{"index":1,"codec_type":"video","duration":"30.25"}],"format":{}}""";

        Assert.Equal(TimeSpan.FromSeconds(30.25), FfprobeParser.TryParse(json)!.Duration);
    }

    [Theory]
    [InlineData("und")]
    [InlineData("")]
    [InlineData("zxx")]
    public void Parse_TreatsUndeterminedLanguageAsNone(string language)
    {
        var json = $$$"""{"streams":[{"index":0,"codec_type":"audio","tags":{"language":"{{{language}}}"}}],"format":{}}""";

        var info = FfprobeParser.TryParse(json)!;

        Assert.Null(info.Audio[0].Language);
        Assert.Empty(info.AudioLanguages);
    }

    [Fact]
    public void Parse_OfAnEmptyObject_GivesAnEmptyInfo_NotAnError()
    {
        var info = FfprobeParser.TryParse("{}")!;

        Assert.Null(info.Container);
        Assert.Null(info.Duration);
        Assert.Empty(info.Video);
        Assert.Empty(info.Audio);
        Assert.Empty(info.Chapters);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Parse_OfSomethingThatIsNotAnObject_ReturnsNull(string json)
    {
        Assert.Null(FfprobeParser.TryParse(json));
    }

    [Fact]
    public void Parse_ToleratesWrongTypesAndOddValues_WithoutThrowing()
    {
        const string json = """
            {"streams":[null,5,{"index":"x","codec_type":"video","width":"wide","height":-1,"bit_rate":"-5","tags":{"language":3,"title":null}},{"codec_type":"subtitle"}],
             "chapters":[{"start_time":"abc"},{"start_time":"1.0","end_time":"0.5","tags":{"title":"Odd"}}],
             "format":{"duration":"-1","bit_rate":"fast","tags":"none"}}
            """;

        var info = FfprobeParser.TryParse(json)!;

        Assert.Null(info.Duration);
        Assert.Null(info.BitRate);
        Assert.Null(info.PrimaryVideo!.Width);
        Assert.Null(info.PrimaryVideo.BitRate);
        Assert.Single(info.Subtitles);
        Assert.Equal("Odd", Assert.Single(info.Chapters).Title);
    }

    [Fact]
    public void Parse_GivesStreamsTheirOwnIndexes_InFileOrder()
    {
        var info = FfprobeParser.TryParse(Snapshot.Read("multi.mkv.json"))!;

        Assert.Equal([0], info.Video.Select(v => v.Index));
        Assert.Equal([1, 2], info.Audio.Select(a => a.Index));
        Assert.Equal([3, 4], info.Subtitles.Select(s => s.Index));
    }
}
