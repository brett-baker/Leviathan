using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Media;
using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Tests;

/// <summary>The text of the details pane (spec §9.2).</summary>
public sealed class FileDetailsTests
{
    private static MediaInfo Info() => new(
        "matroska", "Matroska", TimeSpan.FromMinutes(102), 18_400_000, 1,
        [new VideoStreamInfo(0, "hevc", "Main 10", 3840, 2160, 23.976, null, "yuv420p10le", 10, HdrKind.Hdr10, false, null, null)],
        [new AudioStreamInfo(1, "eac3", 6, "5.1", 48000, 640_000, "eng", null, true), new AudioStreamInfo(2, "aac", 2, null, 44100, null, "fra", "Commentary", false)],
        [new SubtitleStreamInfo(3, "subrip", "eng", null, false, true)],
        [new ChapterInfo(TimeSpan.Zero, TimeSpan.FromMinutes(5), "One")],
        new Dictionary<string, string> { ["title"] = "Demo" });

    [Fact]
    public void MediaSummary_ReadsLikeTheSpecExample()
    {
        Assert.Equal("1h 42m · 3840×2160 · HEVC 10-bit HDR10 · 23.976 fps · 18.4 Mb/s", FileDetails.MediaSummary(Info()));
    }

    [Fact]
    public void MediaSummary_LeavesOutWhatTheFileDoesNotSay()
    {
        var bare = new MediaInfo(null, null, null, null, null, [], [], [], [], new Dictionary<string, string>());

        Assert.Equal(string.Empty, FileDetails.MediaSummary(bare));
    }

    [Fact]
    public void Media_HasARowPerStream_WithLanguageAndChannels()
    {
        var section = FileDetails.Media(Info());

        Assert.Contains(section.Rows, r => r.Label == "Audio 1" && r.Value.StartsWith("eng · EAC3 · 5.1", StringComparison.Ordinal) && r.Value.EndsWith("default", StringComparison.Ordinal));
        Assert.Contains(section.Rows, r => r.Label == "Audio 2" && r.Value.Contains("2 ch", StringComparison.Ordinal) && r.Value.Contains("Commentary", StringComparison.Ordinal));
        Assert.Contains(section.Rows, r => r.Label == "Subtitle 3" && r.Value.StartsWith("eng · subrip", StringComparison.Ordinal));
        Assert.Contains(section.Rows, r => r.Label == "Chapters" && r.Value == "1");
        Assert.Contains(section.Rows, r => r.Label == "title" && r.Value == "Demo");
    }

    [Fact]
    public void Media_CoverArtIsNotListedAsAVideoStream()
    {
        var info = Info() with { Video = [.. Info().Video, new VideoStreamInfo(9, "mjpeg", null, 500, 500, null, null, null, null, HdrKind.None, true, null, null)] };

        Assert.DoesNotContain(FileDetails.Media(info).Rows, r => r.Label == "Video 9");
    }

    [Fact]
    public void General_ShowsThePathAsCopyable_AndTheAttributes()
    {
        var entry = new FileEntry("clip.mkv", LocationPath.Parse(@"C:\v\clip.mkv"), FileEntryKind.File, 2048, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var rows = FileDetails.General(entry).Rows;

        Assert.True(rows.Single(r => r.Label == "Path").Copyable);
        Assert.Equal(@"C:\v\clip.mkv", rows.Single(r => r.Label == "Path").Value);
        Assert.Equal("MKV file", rows.Single(r => r.Label == "Type").Value);
        Assert.Equal("Read-only, Hidden", rows.Single(r => r.Label == "Attributes").Value);
        Assert.StartsWith("2 KB", rows.Single(r => r.Label == "Size").Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(42, "42s")]
    [InlineData(425, "7m 05s")]
    [InlineData(6120, "1h 42m")]
    public void Duration_IsShortAndReadable(int seconds, string expected) =>
        Assert.Equal(expected, FileDetails.Duration(TimeSpan.FromSeconds(seconds)));
}
