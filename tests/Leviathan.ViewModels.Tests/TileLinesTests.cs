using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Media;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class TileLinesTests
{
    private sealed class Source(Dictionary<string, MediaInfoResult> answers) : IMediaInfoSource
    {
        public Task<IReadOnlyDictionary<string, MediaInfoResult>> PeekAsync(IReadOnlyList<MediaFileRef> files, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, MediaInfoResult>>(files.Where(f => answers.ContainsKey(f.Path)).ToDictionary(f => f.Path, f => answers[f.Path], StringComparer.OrdinalIgnoreCase));

        public Task<MediaInfoResult> ProbeAsync(MediaFileRef file, CancellationToken cancellationToken) =>
            answers.TryGetValue(file.Path, out var answer) ? Task.FromResult(answer) : new TaskCompletionSource<MediaInfoResult>().Task;
    }

    private sealed class InlineUi : IUiContext
    {
        public void Post(Action action) => action();
    }

    private static readonly DateTimeOffset Modified = new(2026, 5, 1, 12, 30, 0, TimeSpan.Zero);

    private static FileEntry File(string name, long size = 1_500_000) =>
        new(name, LocationPath.Parse($@"D:\v\{name}"), FileEntryKind.File, size, DateTimeOffset.UnixEpoch, Modified, FileAttributes.Normal);

    private static MediaInfoResult Video() => new(MediaInfoState.Ok, new MediaInfo(
        "matroska", null, TimeSpan.FromSeconds(203), 4_000_000, null,
        [new VideoStreamInfo(0, "h264", null, 1920, 1080, 29.97, null, null, 8, HdrKind.None, false, null, null)], [], [], [], new Dictionary<string, string>()), null);

    private static MediaInfoResult Song() => new(MediaInfoState.Ok, new MediaInfo(
        "flac", null, TimeSpan.FromSeconds(20), 900_000, null,
        [], [new AudioStreamInfo(0, "flac", 2, null, 44100, null, null, null, true)], [], [], new Dictionary<string, string>()), null);

    private static async Task<MediaInfoTracker> TrackedAsync(Dictionary<string, MediaInfoResult> answers, params FileEntry[] files)
    {
        var tracker = new MediaInfoTracker(new Source(answers), new InlineUi());
        tracker.SetVisible(files);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && files.Any(f => tracker.TextFor(f, MediaColumnKind.Duration) == "…"))
        {
            await Task.Delay(5);
        }

        return tracker;
    }

    [Fact]
    public async Task For_AVideo_ListsDurationResolutionAndFrameRate_ThenSizeAndDate()
    {
        var clip = File("clip.mkv");
        using var tracker = await TrackedAsync(new() { [@"D:\v\clip.mkv"] = Video() }, clip);

        var (facts, meta) = TileLines.For(clip, tracker);

        Assert.Equal("3:23 · 1920×1080 · 29.97 fps", facts);
        Assert.Equal($"{DisplayFormat.Size(1_500_000)} · {DisplayFormat.Date(Modified)}", meta);
    }

    [Fact]
    public async Task For_ASong_ListsDurationCodecAndBitrate()
    {
        var song = File("song.flac");
        using var tracker = await TrackedAsync(new() { [@"D:\v\song.flac"] = Song() }, song);

        var (facts, _) = TileLines.For(song, tracker);

        Assert.Equal($"0:20 · FLAC · {MediaColumns.Bitrate(900_000)}", facts);
    }

    [Fact]
    public async Task For_AnUnreadableFile_ShowsADash()
    {
        var broken = File("broken.mp4");
        using var tracker = await TrackedAsync(new() { [@"D:\v\broken.mp4"] = new(MediaInfoState.Unreadable, null, "bad") }, broken);

        Assert.Equal("—", TileLines.For(broken, tracker).Facts);
    }

    [Fact]
    public async Task For_AFileStillWaitingToBeRead_ShowsAnEllipsis()
    {
        var clip = File("clip.mkv");
        using var tracker = new MediaInfoTracker(new Source([]), new InlineUi());
        tracker.SetVisible([clip]);
        await Task.Delay(50);

        Assert.Equal("…", TileLines.For(clip, tracker).Facts);
    }

    [Fact]
    public void For_ANonMediaFile_HasOnlySizeAndDate()
    {
        using var tracker = new MediaInfoTracker(new Source([]), new InlineUi());

        var (facts, meta) = TileLines.For(File("notes.txt", 2048), tracker);

        Assert.Equal(string.Empty, facts);
        Assert.Equal($"{DisplayFormat.Size(2048)} · {DisplayFormat.Date(Modified)}", meta);
    }

    [Fact]
    public void For_AFolder_HasOnlyTheDate()
    {
        var folder = new FileEntry("sub", LocationPath.Parse(@"D:\v\sub"), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, Modified, FileAttributes.Directory);

        Assert.Equal((string.Empty, DisplayFormat.Date(Modified)), TileLines.For(folder, null));
    }

    [Fact]
    public void ItemFlow_TilesAndGallery_AreFlowLayoutsWithTheirOwnGeometry()
    {
        Assert.True(ItemFlow.IsFlow(ViewLayout.Tiles));
        Assert.True(ItemFlow.IsFlow(ViewLayout.Gallery));
        Assert.False(ItemFlow.IsFlow(ViewLayout.Details));

        // Tiles and Gallery ignore the tab's thumbnail size; Thumbnails follows it.
        Assert.Equal(ItemFlow.TileWidth, ItemFlow.ItemWidth(ViewLayout.Tiles, 512));
        Assert.Equal(ItemFlow.GalleryThumbnailSize, ItemFlow.PictureEdge(ViewLayout.Gallery, 64));
        Assert.Equal(ItemFlow.CellHeight(ItemFlow.GalleryThumbnailSize), ItemFlow.ItemHeight(ViewLayout.Gallery, 64));
        Assert.Equal(ItemFlow.CellWidth(200), ItemFlow.ItemWidth(ViewLayout.Thumbnails, 200));
        Assert.Equal(ItemFlow.ListItemWidth, ItemFlow.ItemWidth(ViewLayout.List, 200));
    }
}
