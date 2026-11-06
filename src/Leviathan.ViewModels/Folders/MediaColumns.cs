using System.Globalization;
using Leviathan.Media;

namespace Leviathan.ViewModels.Folders;

/// <summary>The media columns of the Details view (spec §8.2): what each one shows from a file's <see cref="MediaInfo"/>.</summary>
public enum MediaColumnKind
{
    /// <summary>Running time.</summary>
    Duration,

    /// <summary>Width × height of the main video.</summary>
    Resolution,

    /// <summary>Frames per second of the main video.</summary>
    FrameRate,

    /// <summary>Codec of the main video.</summary>
    VideoCodec,

    /// <summary>Codec of the main audio.</summary>
    AudioCodec,

    /// <summary>Overall bit rate.</summary>
    Bitrate,

    /// <summary>Container format.</summary>
    Container,

    /// <summary>SDR, HDR10, HLG or Dolby Vision.</summary>
    Hdr,

    /// <summary>Audio channels of the main audio (2.0, 5.1).</summary>
    Channels,

    /// <summary>Audio languages.</summary>
    AudioLanguages,

    /// <summary>Subtitle tracks.</summary>
    Subtitles,
}

/// <summary>What a media column sorts a file by: a number, or text when it has no number. A file with no value sorts before every file that has one.</summary>
/// <param name="Number">The number (duration in seconds, pixels, bits per second, channels).</param>
/// <param name="Text">The text, for columns that sort by words.</param>
public readonly record struct MediaSortKey(double? Number, string? Text)
{
    /// <summary>No value.</summary>
    public static MediaSortKey None => default;

    /// <summary>Orders two keys: unknown first, then numbers, then text in natural order.</summary>
    public static int Compare(MediaSortKey a, MediaSortKey b)
    {
        if (a.Number is { } an && b.Number is { } bn)
        {
            return an.CompareTo(bn);
        }

        if (a.Text is { } at && b.Text is { } bt)
        {
            return string.Compare(at, bt, StringComparison.OrdinalIgnoreCase);
        }

        // One side has no value: it comes first.
        return (a.Number is null && a.Text is null ? 0 : 1).CompareTo(b.Number is null && b.Text is null ? 0 : 1);
    }
}

/// <summary>Text and sort values of the media columns, kept apart from the grid so they are tested without a window (spec §8.2).</summary>
public static class MediaColumns
{
    /// <summary>Every media column, with the id the column chooser and the view settings use.</summary>
    public static IReadOnlyList<(string Id, string Title, MediaColumnKind Kind)> All { get; } =
    [
        ("duration", "Duration", MediaColumnKind.Duration),
        ("resolution", "Resolution", MediaColumnKind.Resolution),
        ("framerate", "Frame rate", MediaColumnKind.FrameRate),
        ("videocodec", "Video codec", MediaColumnKind.VideoCodec),
        ("audiocodec", "Audio codec", MediaColumnKind.AudioCodec),
        ("bitrate", "Bitrate", MediaColumnKind.Bitrate),
        ("container", "Container", MediaColumnKind.Container),
        ("hdr", "HDR", MediaColumnKind.Hdr),
        ("channels", "Audio channels", MediaColumnKind.Channels),
        ("audiolanguages", "Audio language(s)", MediaColumnKind.AudioLanguages),
        ("subtitles", "Subtitle tracks", MediaColumnKind.Subtitles),
    ];

    /// <summary>True for the columns that show numbers, which are right-aligned.</summary>
    public static bool IsNumeric(MediaColumnKind kind) => kind is MediaColumnKind.Duration or MediaColumnKind.FrameRate or MediaColumnKind.Bitrate or MediaColumnKind.Channels;

    /// <summary>The cell text for <paramref name="kind"/>; empty when the file has no such thing (a song has no resolution).</summary>
    public static string Text(MediaColumnKind kind, MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return kind switch
        {
            MediaColumnKind.Duration => info.Duration is { } d ? Duration(d) : string.Empty,
            MediaColumnKind.Resolution => info.PrimaryVideo is { Width: > 0, Height: > 0 } v ? $"{v.Width}×{v.Height}" : string.Empty,
            MediaColumnKind.FrameRate => info.PrimaryVideo?.FrameRate is { } f ? FrameRate(f) : string.Empty,
            MediaColumnKind.VideoCodec => info.PrimaryVideo?.Codec is { } vc ? VideoCodec(vc) : string.Empty,
            MediaColumnKind.AudioCodec => info.PrimaryAudio?.Codec is { } ac ? AudioCodec(ac) : string.Empty,
            MediaColumnKind.Bitrate => info.BitRate is { } b ? Bitrate(b) : string.Empty,
            MediaColumnKind.Container => info.Container is { } c ? Container(c) : string.Empty,
            MediaColumnKind.Hdr => info.PrimaryVideo is { } hv ? Hdr(hv.Hdr) : string.Empty,
            MediaColumnKind.Channels => info.PrimaryAudio is { } a ? Channels(a) : string.Empty,
            MediaColumnKind.AudioLanguages => string.Join(", ", info.AudioLanguages),
            MediaColumnKind.Subtitles => Subtitles(info),
            _ => string.Empty,
        };
    }

    /// <summary>What <paramref name="kind"/> sorts <paramref name="info"/> by.</summary>
    public static MediaSortKey SortKey(MediaColumnKind kind, MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return kind switch
        {
            MediaColumnKind.Duration => info.Duration is { } d ? new(d.TotalSeconds, null) : MediaSortKey.None,
            MediaColumnKind.Resolution => info.PrimaryVideo is { Width: > 0, Height: > 0 } v ? new((double)v.Width!.Value * v.Height!.Value, null) : MediaSortKey.None,
            MediaColumnKind.FrameRate => info.PrimaryVideo?.FrameRate is { } f ? new(f, null) : MediaSortKey.None,
            MediaColumnKind.Bitrate => info.BitRate is { } b ? new(b, null) : MediaSortKey.None,
            MediaColumnKind.Channels => info.PrimaryAudio?.Channels is { } c ? new(c, null) : MediaSortKey.None,
            MediaColumnKind.Hdr => info.PrimaryVideo is { } hv ? new((double)hv.Hdr, null) : MediaSortKey.None,
            MediaColumnKind.Subtitles => new(info.Subtitles.Count, null),
            _ => Text(kind, info) is { Length: > 0 } text ? new(null, text) : MediaSortKey.None,
        };
    }

    /// <summary>"1:02:03" from an hour up, "2:05" below (the way a player shows it).</summary>
    public static string Duration(TimeSpan duration)
    {
        var whole = (long)Math.Round(duration.TotalSeconds);
        var hours = whole / 3600;
        var minutes = whole % 3600 / 60;
        var seconds = whole % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:D2}:{seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:D2}");
    }

    /// <summary>"24", "23.976", "29.97": as many decimals as the rate needs, at most three.</summary>
    public static string FrameRate(double framesPerSecond) =>
        framesPerSecond.ToString("0.###", CultureInfo.InvariantCulture) + " fps";

    /// <summary>"12.4 Mb/s", or "640 kb/s" below a megabit.</summary>
    public static string Bitrate(long bitsPerSecond) => bitsPerSecond >= 1_000_000
        ? (bitsPerSecond / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Mb/s"
        : (bitsPerSecond / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kb/s";

    /// <summary>Friendly names for the codecs people recognise; anything else in capitals.</summary>
    public static string VideoCodec(string codec) => codec.ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg4" => "MPEG-4",
        "mpeg2video" => "MPEG-2",
        "mpeg1video" => "MPEG-1",
        "vc1" => "VC-1",
        "wmv3" => "WMV",
        "prores" => "ProRes",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>Friendly names for the audio codecs people recognise; PCM variants are all "PCM".</summary>
    public static string AudioCodec(string codec) => codec.ToLowerInvariant() switch
    {
        "aac" => "AAC",
        "ac3" => "AC-3",
        "eac3" => "E-AC-3",
        "dts" => "DTS",
        "truehd" => "TrueHD",
        "flac" => "FLAC",
        "mp3" => "MP3",
        "mp2" => "MP2",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "alac" => "ALAC",
        var pcm when pcm.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>The container by the name people use for the file.</summary>
    public static string Container(string container) => container.ToLowerInvariant() switch
    {
        "matroska" => "Matroska",
        "mov" => "MP4/MOV",
        "avi" => "AVI",
        "mpegts" => "MPEG-TS",
        "mpeg" => "MPEG-PS",
        "asf" => "ASF",
        "flv" => "FLV",
        "ogg" => "Ogg",
        "wav" => "WAV",
        "flac" => "FLAC",
        "mp3" => "MP3",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>"SDR", "HDR10", "HLG" or "Dolby Vision".</summary>
    public static string Hdr(HdrKind kind) => kind switch
    {
        HdrKind.Hdr10 => "HDR10",
        HdrKind.Hlg => "HLG",
        HdrKind.DolbyVision => "Dolby Vision",
        _ => "SDR",
    };

    /// <summary>"2.0", "5.1", "7.1" (from the layout, or the channel count with a ".0" when the layout is not a standard one).</summary>
    public static string Channels(AudioStreamInfo audio)
    {
        var layout = audio.ChannelLayout?.ToLowerInvariant();
        var named = layout switch
        {
            "mono" => "1.0",
            "stereo" => "2.0",
            "2.1" => "2.1",
            "3.0" => "3.0",
            "quad" or "4.0" => "4.0",
            "5.0" or "5.0(side)" => "5.0",
            "5.1" or "5.1(side)" => "5.1",
            "6.1" => "6.1",
            "7.1" or "7.1(wide)" or "7.1(wide-side)" => "7.1",
            _ => null,
        };
        return named ?? (audio.Channels is { } count and > 0 ? string.Create(CultureInfo.InvariantCulture, $"{count} ch") : string.Empty);
    }

    private static string Subtitles(MediaInfo info)
    {
        if (info.Subtitles.Count == 0)
        {
            return string.Empty;
        }

        var languages = info.SubtitleLanguages;
        return languages.Count == 0
            ? info.Subtitles.Count.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{info.Subtitles.Count} ({string.Join(", ", languages)})");
    }
}
