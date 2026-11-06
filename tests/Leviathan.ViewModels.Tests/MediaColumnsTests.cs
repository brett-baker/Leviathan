using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Media;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class MediaColumnsTests
{
    private static MediaInfo Info(string json) => FfprobeParser.TryParse(json)!;

    private static readonly MediaInfo Movie = Info("""
        {"streams":[
          {"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"avg_frame_rate":"24000/1001","color_transfer":"smpte2084","pix_fmt":"yuv420p10le"},
          {"index":1,"codec_type":"audio","codec_name":"eac3","channels":6,"channel_layout":"5.1(side)","tags":{"language":"eng"},"disposition":{"default":1}},
          {"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"channel_layout":"stereo","tags":{"language":"jpn"}},
          {"index":3,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"}},
          {"index":4,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"fre"}}],
         "format":{"format_name":"matroska,webm","duration":"5415.5","bit_rate":"24500000"}}
        """);

    private static readonly MediaInfo Song = Info("""{"streams":[{"index":0,"codec_type":"audio","codec_name":"flac","channels":2,"channel_layout":"stereo"}],"format":{"format_name":"flac","duration":"203.2","bit_rate":"900000"}}""");

    [Theory]
    [InlineData(MediaColumnKind.Duration, "1:30:16")]
    [InlineData(MediaColumnKind.Resolution, "3840×2160")]
    [InlineData(MediaColumnKind.FrameRate, "23.976 fps")]
    [InlineData(MediaColumnKind.VideoCodec, "HEVC")]
    [InlineData(MediaColumnKind.AudioCodec, "E-AC-3")]
    [InlineData(MediaColumnKind.Bitrate, "24.5 Mb/s")]
    [InlineData(MediaColumnKind.Container, "Matroska")]
    [InlineData(MediaColumnKind.Hdr, "HDR10")]
    [InlineData(MediaColumnKind.Channels, "5.1")]
    [InlineData(MediaColumnKind.AudioLanguages, "eng, jpn")]
    [InlineData(MediaColumnKind.Subtitles, "2 (eng, fre)")]
    public void Text_ForAMovie_ReadsLikeAPlayerWouldShowIt(MediaColumnKind kind, string expected)
    {
        Assert.Equal(expected, MediaColumns.Text(kind, Movie));
    }

    [Theory]
    [InlineData(MediaColumnKind.Duration, "3:23")]
    [InlineData(MediaColumnKind.AudioCodec, "FLAC")]
    [InlineData(MediaColumnKind.Bitrate, "900 kb/s")]
    [InlineData(MediaColumnKind.Channels, "2.0")]
    [InlineData(MediaColumnKind.Resolution, "")]
    [InlineData(MediaColumnKind.FrameRate, "")]
    [InlineData(MediaColumnKind.VideoCodec, "")]
    [InlineData(MediaColumnKind.Hdr, "")]
    [InlineData(MediaColumnKind.Subtitles, "")]
    [InlineData(MediaColumnKind.AudioLanguages, "")]
    public void Text_ForASong_LeavesOutWhatASongDoesNotHave(MediaColumnKind kind, string expected)
    {
        Assert.Equal(expected, MediaColumns.Text(kind, Song));
    }

    [Theory]
    [InlineData(59.4, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(3599.6, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(0, "0:00")]
    public void Duration_RoundsToTheSecond_AndShowsHoursOnlyWhenThereAreSome(double seconds, string expected)
    {
        Assert.Equal(expected, MediaColumns.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(24.0, "24 fps")]
    [InlineData(29.97003, "29.97 fps")]
    [InlineData(59.94006, "59.94 fps")]
    [InlineData(25.0, "25 fps")]
    public void FrameRate_ShowsAsManyDecimalsAsItNeeds(double rate, string expected)
    {
        Assert.Equal(expected, MediaColumns.FrameRate(rate));
    }

    [Theory]
    [InlineData("h264", "H.264")]
    [InlineData("mystery", "MYSTERY")]
    public void VideoCodec_UsesTheFamiliarName(string codec, string expected) => Assert.Equal(expected, MediaColumns.VideoCodec(codec));

    [Theory]
    [InlineData("pcm_s16le", "PCM")]
    [InlineData("truehd", "TrueHD")]
    [InlineData("dts", "DTS")]
    public void AudioCodec_UsesTheFamiliarName(string codec, string expected) => Assert.Equal(expected, MediaColumns.AudioCodec(codec));

    [Fact]
    public void Hdr_OfAnSdrVideo_SaysSdr_SoTheColumnIsNotBlank()
    {
        var sdr = Info("""{"streams":[{"index":0,"codec_type":"video","codec_name":"h264"}],"format":{}}""");

        Assert.Equal("SDR", MediaColumns.Text(MediaColumnKind.Hdr, sdr));
    }

    [Fact]
    public void Channels_WithAnUnusualLayout_FallsBackToTheCount()
    {
        var odd = Info("""{"streams":[{"index":0,"codec_type":"audio","channels":9,"channel_layout":"weird"}],"format":{}}""");

        Assert.Equal("9 ch", MediaColumns.Text(MediaColumnKind.Channels, odd));
    }

    [Fact]
    public void SortKey_OrdersByTheNumberBehindTheText_NotByTheText()
    {
        var long1 = Info("""{"streams":[],"format":{"duration":"9.0"}}""");
        var long2 = Info("""{"streams":[],"format":{"duration":"100.0"}}""");

        // "9:00" would sort after "1:40" as text; as numbers 9 s comes first.
        Assert.True(MediaSortKey.Compare(MediaColumns.SortKey(MediaColumnKind.Duration, long1), MediaColumns.SortKey(MediaColumnKind.Duration, long2)) < 0);
        Assert.True(MediaSortKey.Compare(MediaColumns.SortKey(MediaColumnKind.Resolution, Movie), MediaSortKey.None) > 0);
    }

    [Fact]
    public void SortKey_ForTextColumns_SortsByTheText()
    {
        var a = MediaColumns.SortKey(MediaColumnKind.VideoCodec, Movie);

        Assert.Equal("HEVC", a.Text);
        Assert.Null(a.Number);
        Assert.True(MediaSortKey.Compare(MediaSortKey.None, a) < 0);
        Assert.Equal(0, MediaSortKey.Compare(MediaSortKey.None, MediaSortKey.None));
    }

    [Fact]
    public void EveryMediaColumn_Exists_AndIsAvailableInTheChooser()
    {
        foreach (var (id, _, kind) in MediaColumns.All)
        {
            Assert.True(FileColumns.TryGet(id, out var column), id);
            Assert.Equal(kind, column.Media);
            Assert.True(ColumnCatalog.All.Single(c => c.Id == id).Available, id);
        }

        Assert.Equal(11, FileColumns.Media.Count);
    }

    // The view -----------------------------------------------------------------------------------------------------------

    private static FileEntry File(string name) =>
        new(name, LocationPath.Parse($@"C:\v\{name}"), FileEntryKind.File, 1000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Fact]
    public void Sorting_ByDuration_PutsUnreadFilesFirst_ThenShortestToLongest_AndMovesRowsWhenInformationArrives()
    {
        var known = new Dictionary<string, MediaSortKey>();
        var source = new RangeObservableCollection<FileEntry>();
        var view = new FolderItemsView(source);
        view.SetMediaKeys((entry, kind) => known.GetValueOrDefault(entry.Name));
        source.AddRange([File("a.mkv"), File("b.mkv"), File("c.mkv")]);
        known["a.mkv"] = new(300, null);
        known["b.mkv"] = new(60, null);

        view.SortBy("duration");

        Assert.Equal(["c.mkv", "b.mkv", "a.mkv"], view.Rows.Select(r => r.Name));

        // c.mkv is read and turns out to be the longest.
        known["c.mkv"] = new(900, null);
        view.ResortByMedia();

        Assert.Equal(["b.mkv", "a.mkv", "c.mkv"], view.Rows.Select(r => r.Name));
        Assert.True(view.SortsByMedia);
    }

    [Fact]
    public void ResortByMedia_WhenNotSortedByAMediaColumn_DoesNothing()
    {
        var source = new RangeObservableCollection<FileEntry>();
        var view = new FolderItemsView(source);
        view.SetMediaKeys((_, _) => MediaSortKey.None);
        source.AddRange([File("b.mkv"), File("a.mkv")]);

        view.ResortByMedia();

        Assert.False(view.SortsByMedia);
        Assert.Equal(["a.mkv", "b.mkv"], view.Rows.Select(r => r.Name));
    }

    // The tab ------------------------------------------------------------------------------------------------------------

    private sealed class CountingSource : IMediaInfoSource
    {
        private readonly List<string> _probed = [];

        public List<string> Probed
        {
            get
            {
                lock (_probed)
                {
                    return [.. _probed];
                }
            }
        }

        public Task<IReadOnlyDictionary<string, MediaInfoResult>> PeekAsync(IReadOnlyList<MediaFileRef> files, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, MediaInfoResult>>(new Dictionary<string, MediaInfoResult>());

        public Task<MediaInfoResult> ProbeAsync(MediaFileRef file, CancellationToken cancellationToken)
        {
            lock (_probed)
            {
                _probed.Add(file.Path);
            }

            return Task.FromResult(new MediaInfoResult(MediaInfoState.Ok, Song, null));
        }
    }

    private static async Task<(TabViewModel Tab, CountingSource Source)> VideoTabAsync()
    {
        var source = new CountingSource();
        var services = new TabServices(new LocationProviderRegistry([TabTestSupport.Tree()]), new InlineUiContext(), new FolderViewRules(), TimeProvider.System,
            () => TabTestSupport.P(@"C:\Home"), MediaInfo: source);
        var tab = new TabViewModel(services, TabTestSupport.P(@"C:\v"));
        await tab.ReloadAsync(CancellationToken.None);
        return (tab, source);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

    [Fact]
    public async Task ATab_WithNoMediaColumn_ReadsNothing_NoMatterHowManyVideosAreOnScreen()
    {
        var (tab, source) = await VideoTabAsync();
        using (tab)
        {
            Assert.False(tab.ShowsMediaColumns);

            tab.RequestMedia([.. tab.Items.Rows]);
            await Task.Delay(150);

            Assert.Empty(source.Probed);
        }
    }

    [Fact]
    public async Task ATab_WithAMediaColumn_ReadsTheRowsOnScreen()
    {
        var (tab, source) = await VideoTabAsync();
        using (tab)
        {
            tab.Columns.SetVisible("duration", true);

            tab.RequestMedia([.. tab.Items.Rows]);

            Assert.True(tab.ShowsMediaColumns);
            Assert.True(await UntilAsync(() => source.Probed.Count == 3));
        }
    }

    [Fact]
    public async Task SortingByAMediaColumn_ReadsTheWholeFolder_AndSortingByNameStopsIt()
    {
        var (tab, source) = await VideoTabAsync();
        using (tab)
        {
            tab.SortBy("duration");

            Assert.True(await UntilAsync(() => source.Probed.Count == 3));
            Assert.Equal(1.0, tab.Media.Progress.Fraction);

            tab.SortBy("name");

            Assert.False(tab.Media.Progress.IsRunning);
            Assert.Equal(0, tab.Media.Progress.Total);
        }
    }

    [Fact]
    public async Task TheMediaCells_ShowAPlaceholderForAWaitingRow_ThenTheValue()
    {
        var (tab, _) = await VideoTabAsync();
        using (tab)
        {
            tab.Columns.SetVisible("duration", true);
            var clip = tab.Items.Rows.First(r => r.Name == "x.mkv");
            tab.RequestMedia([clip]);

            Assert.True(await UntilAsync(() => tab.Media.TextFor(clip, MediaColumnKind.Duration) == "3:23"));
            Assert.Equal(string.Empty, tab.Media.TextFor(tab.Items.Rows.First(r => r.Name == "x.mkv"), MediaColumnKind.Resolution));
        }
    }

    [Fact]
    public void TheMediaCells_OfANonMediaFileOrAFolder_AreEmpty()
    {
        using var tracker = new MediaInfoTracker(new CountingSource(), new InlineUiContext());

        Assert.Equal(string.Empty, tracker.TextFor(File("notes.txt"), MediaColumnKind.Duration));
        Assert.Equal(string.Empty, tracker.TextFor(File("clip.mkv"), MediaColumnKind.Duration));
    }
}
