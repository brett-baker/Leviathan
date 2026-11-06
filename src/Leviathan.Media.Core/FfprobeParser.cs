using System.Globalization;
using System.Text.Json;

namespace Leviathan.Media;

/// <summary>
/// Turns ffprobe's JSON into a <see cref="MediaInfo"/> (spec §9.3). ffprobe prints most numbers as strings and leaves out whatever it
/// does not know, so every field is optional: a missing or odd value becomes <c>null</c>, never an exception and never a made-up zero.
/// Only text that is not a JSON object at all is rejected.
/// </summary>
public static class FfprobeParser
{
    /// <summary>Parses <paramref name="json"/>; returns null when it is not a JSON object.</summary>
    public static MediaInfo? TryParse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var format = Object(root, "format");
            var video = new List<VideoStreamInfo>();
            var audio = new List<AudioStreamInfo>();
            var subtitles = new List<SubtitleStreamInfo>();
            TimeSpan? longestStream = null;
            foreach (var stream in Array(root, "streams"))
            {
                if (stream.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var index = Int(stream, "index") ?? (video.Count + audio.Count + subtitles.Count);
                var tags = Tags(stream);
                var disposition = Object(stream, "disposition");
                var language = Language(tags);
                var title = tags.GetValueOrDefault("title");
                if (Seconds(stream, "duration") is { } streamDuration && (longestStream is null || streamDuration > longestStream))
                {
                    longestStream = streamDuration;
                }

                switch (String(stream, "codec_type"))
                {
                    case "video":
                        video.Add(Video(stream, index, language, title, Flag(disposition, "attached_pic")));
                        break;
                    case "audio":
                        audio.Add(new AudioStreamInfo(
                            index, String(stream, "codec_name"), Int(stream, "channels"), String(stream, "channel_layout"), Int(stream, "sample_rate"),
                            Long(stream, "bit_rate"), language, title, Flag(disposition, "default")));
                        break;
                    case "subtitle":
                        subtitles.Add(new SubtitleStreamInfo(index, String(stream, "codec_name"), language, title, Flag(disposition, "forced"), Flag(disposition, "default")));
                        break;
                }
            }

            var chapters = new List<ChapterInfo>();
            foreach (var chapter in Array(root, "chapters"))
            {
                if (chapter.ValueKind == JsonValueKind.Object && Seconds(chapter, "start_time") is { } start)
                {
                    var end = Seconds(chapter, "end_time") ?? start;
                    chapters.Add(new ChapterInfo(start, end, Tags(chapter).GetValueOrDefault("title")));
                }
            }

            return new MediaInfo(
                Container: FirstName(String(format, "format_name")),
                ContainerName: String(format, "format_long_name"),
                Duration: Seconds(format, "duration") ?? longestStream,
                BitRate: Long(format, "bit_rate"),
                SizeBytes: Long(format, "size"),
                Video: video,
                Audio: audio,
                Subtitles: subtitles,
                Chapters: chapters,
                Tags: Tags(format));
        }
    }

    private static VideoStreamInfo Video(JsonElement stream, int index, string? language, string? title, bool coverArt) => new(
        index,
        String(stream, "codec_name"),
        String(stream, "profile"),
        Int(stream, "width"),
        Int(stream, "height"),
        coverArt ? null : FrameRate(stream),
        Long(stream, "bit_rate"),
        String(stream, "pix_fmt"),
        Int(stream, "bits_per_raw_sample") ?? BitDepthFromPixelFormat(String(stream, "pix_fmt")),
        Hdr(stream),
        coverArt,
        language,
        title);

    // avg_frame_rate is what a player shows; r_frame_rate (the base rate) is the fallback when the average is unknown ("0/0").
    private static double? FrameRate(JsonElement stream) =>
        Rational(String(stream, "avg_frame_rate")) ?? Rational(String(stream, "r_frame_rate"));

    private static double? Rational(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var parts = text.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0 && numerator > 0)
        {
            return numerator / denominator;
        }

        return parts.Length == 1 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var whole) && whole > 0 ? whole : null;
    }

    private static HdrKind Hdr(JsonElement stream)
    {
        foreach (var data in Array(stream, "side_data_list"))
        {
            if (data.ValueKind == JsonValueKind.Object && String(data, "side_data_type") is { } kind && kind.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
            {
                return HdrKind.DolbyVision;
            }
        }

        return String(stream, "color_transfer") switch
        {
            "smpte2084" => HdrKind.Hdr10,
            "arib-std-b67" => HdrKind.Hlg,
            _ => HdrKind.None,
        };
    }

    private static int? BitDepthFromPixelFormat(string? pixelFormat) => pixelFormat switch
    {
        null => null,
        _ when pixelFormat.Contains("p10", StringComparison.Ordinal) || pixelFormat.EndsWith("10le", StringComparison.Ordinal) || pixelFormat.EndsWith("10be", StringComparison.Ordinal) => 10,
        _ when pixelFormat.Contains("p12", StringComparison.Ordinal) || pixelFormat.EndsWith("12le", StringComparison.Ordinal) || pixelFormat.EndsWith("12be", StringComparison.Ordinal) => 12,
        _ => 8,
    };

    // "mov,mp4,m4a,3gp,3g2,mj2" is one family; the first name is the one people know.
    private static string? FirstName(string? names) => names?.Split(',')[0];

    // ffprobe writes "und" (undetermined) and sometimes "" for no language; neither is a language.
    private static string? Language(Dictionary<string, string> tags) =>
        tags.GetValueOrDefault("language") is { Length: > 0 } language && !language.Equals("und", StringComparison.OrdinalIgnoreCase) && !language.Equals("zxx", StringComparison.OrdinalIgnoreCase)
            ? language
            : null;

    private static Dictionary<string, string> Tags(JsonElement element)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Object(element, "tags") is { ValueKind: JsonValueKind.Object } tagged)
        {
            foreach (var tag in tagged.EnumerateObject())
            {
                if (tag.Value.ValueKind == JsonValueKind.String && tag.Value.GetString() is { } value)
                {
                    tags[tag.Name] = value;
                }
            }
        }

        return tags;
    }

    private static bool Flag(JsonElement element, string name) => Int(element, name) == 1;

    private static JsonElement Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static List<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : [];

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static int? Int(JsonElement element, string name) =>
        String(element, name) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static long? Long(JsonElement element, string name) =>
        String(element, name) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;

    private static TimeSpan? Seconds(JsonElement element, string name) =>
        String(element, name) is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
