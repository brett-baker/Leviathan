namespace Leviathan.Media;

/// <summary>
/// Which files are worth asking ffprobe about (spec §9.3: "lazy, visible items only"). Judged by extension only, because opening
/// every file to find out would defeat the point; a file with a media name that is not media simply comes back unreadable and is
/// remembered, and a media file with an odd extension is still readable from the preview pane or by asking for it.
/// </summary>
public static class MediaFileKinds
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".flv", ".3gp", ".ogv", ".vob", ".divx", ".asf", ".rm", ".rmvb",
    };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".ape", ".alac", ".mka", ".wv", ".dsf",
    };

    /// <summary>True when <paramref name="extension"/> (with its dot) is a video or audio extension.</summary>
    public static bool IsMedia(string extension) => Video.Contains(extension) || Audio.Contains(extension);

    /// <summary>True for a video extension (video containers can hold audio only; the probe says what a file really has).</summary>
    public static bool IsVideo(string extension) => Video.Contains(extension);

    /// <summary>True for an audio-only extension.</summary>
    public static bool IsAudio(string extension) => Audio.Contains(extension);
}
