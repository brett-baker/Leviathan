namespace Leviathan.Media;

/// <summary>The dynamic-range format of a video stream (spec §8.2 "HDR" column).</summary>
public enum HdrKind
{
    /// <summary>Standard dynamic range.</summary>
    None,

    /// <summary>HDR10 (PQ transfer, SMPTE ST 2084).</summary>
    Hdr10,

    /// <summary>Hybrid Log-Gamma (ARIB STD-B67).</summary>
    Hlg,

    /// <summary>Dolby Vision (a DOVI configuration record, with or without an HDR10 base layer).</summary>
    DolbyVision,
}

/// <summary>One video stream. Cover art in a file is a video stream too; <see cref="IsCoverArt"/> tells them apart.</summary>
public sealed record VideoStreamInfo(
    int Index,
    string? Codec,
    string? Profile,
    int? Width,
    int? Height,
    double? FrameRate,
    long? BitRate,
    string? PixelFormat,
    int? BitDepth,
    HdrKind Hdr,
    bool IsCoverArt,
    string? Language,
    string? Title);

/// <summary>One audio stream.</summary>
public sealed record AudioStreamInfo(
    int Index,
    string? Codec,
    int? Channels,
    string? ChannelLayout,
    int? SampleRate,
    long? BitRate,
    string? Language,
    string? Title,
    bool IsDefault);

/// <summary>One subtitle stream.</summary>
public sealed record SubtitleStreamInfo(
    int Index,
    string? Codec,
    string? Language,
    string? Title,
    bool IsForced,
    bool IsDefault);

/// <summary>One chapter.</summary>
public sealed record ChapterInfo(TimeSpan Start, TimeSpan End, string? Title);

/// <summary>
/// What ffprobe knows about a file (spec §9.3), as the rest of Leviathan wants it: typed, with missing values as <c>null</c> rather
/// than guesses, and with no ffprobe wording left in it. The Details columns, the preview pane and triage all read this, never the JSON.
/// </summary>
public sealed record MediaInfo(
    string? Container,
    string? ContainerName,
    TimeSpan? Duration,
    long? BitRate,
    long? SizeBytes,
    IReadOnlyList<VideoStreamInfo> Video,
    IReadOnlyList<AudioStreamInfo> Audio,
    IReadOnlyList<SubtitleStreamInfo> Subtitles,
    IReadOnlyList<ChapterInfo> Chapters,
    IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>The first real video stream (not cover art), which the resolution, frame rate and codec columns describe.</summary>
    public VideoStreamInfo? PrimaryVideo => Video.FirstOrDefault(v => !v.IsCoverArt);

    /// <summary>The first audio stream marked default, else the first.</summary>
    public AudioStreamInfo? PrimaryAudio => Audio.FirstOrDefault(a => a.IsDefault) ?? (Audio.Count > 0 ? Audio[0] : null);

    /// <summary>The distinct audio languages, in stream order (streams with no language are left out).</summary>
    public IReadOnlyList<string> AudioLanguages => [.. Audio.Select(a => a.Language).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The distinct subtitle languages, in stream order.</summary>
    public IReadOnlyList<string> SubtitleLanguages => [.. Subtitles.Select(s => s.Language).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>True when the file has a video stream that is not cover art.</summary>
    public bool HasVideo => PrimaryVideo is not null;
}
