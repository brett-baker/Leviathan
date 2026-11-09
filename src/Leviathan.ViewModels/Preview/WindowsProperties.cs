using System.Globalization;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Preview;

/// <summary>
/// The Windows property system as the details pane shows it (spec §9.2): which <c>System.*</c> properties are asked for and how each
/// value reads (exposure as "1/250 s", GPS as decimal degrees, a list of artists joined). The values come from the shell's property
/// handlers, so EXIF, ID3, Vorbis tags and Office document properties all arrive the same way. Pure text: the reader that fetches the
/// values lives in the app.
/// </summary>
public static class WindowsProperties
{
    private static readonly (string Key, string Label)[] ImageKeys =
    [
        ("System.Photo.CameraManufacturer", "Camera make"),
        ("System.Photo.CameraModel", "Camera model"),
        ("System.Photo.LensModel", "Lens"),
        ("System.Photo.DateTaken", "Taken"),
        ("System.Image.Dimensions", "Dimensions"),
        ("System.Photo.ExposureTime", "Exposure"),
        ("System.Photo.FNumber", "Aperture"),
        ("System.Photo.ISOSpeed", "ISO"),
        ("System.Photo.FocalLength", "Focal length"),
        ("System.Photo.Flash", "Flash"),
        ("System.Image.HorizontalResolution", "Resolution"),
        ("System.Photo.Orientation", "Orientation"),
        ("System.Keywords", "Keywords"),
        ("System.Rating", "Rating"),
    ];

    private static readonly (string Key, string Label)[] AudioKeys =
    [
        ("System.Title", "Title"),
        ("System.Music.Artist", "Artist"),
        ("System.Music.AlbumArtist", "Album artist"),
        ("System.Music.AlbumTitle", "Album"),
        ("System.Music.TrackNumber", "Track"),
        ("System.Music.Genre", "Genre"),
        ("System.Media.Year", "Year"),
        ("System.Media.Duration", "Length"),
        ("System.Audio.EncodingBitrate", "Bit rate"),
        ("System.Audio.SampleRate", "Sample rate"),
        ("System.Audio.ChannelCount", "Channels"),
        ("System.Rating", "Rating"),
    ];

    private static readonly (string Key, string Label)[] DocumentKeys =
    [
        ("System.Title", "Title"),
        ("System.Author", "Author"),
        ("System.Subject", "Subject"),
        ("System.Document.PageCount", "Pages"),
        ("System.Document.WordCount", "Words"),
        ("System.Document.LastAuthor", "Last saved by"),
        ("System.Document.DateSaved", "Saved"),
        ("System.Comment", "Comments"),
    ];

    private static readonly (string Key, string Label)[] OtherKeys =
    [
        ("System.ItemTypeText", "Item type"),
        ("System.Kind", "Kind"),
        ("System.FileOwner", "Owner"),
        ("System.DateAccessed", "Accessed"),
        ("System.Copyright", "Copyright"),
        ("System.ApplicationName", "Application"),
        ("System.Video.FrameWidth", "Frame width"),
        ("System.Video.FrameHeight", "Frame height"),
        ("System.Video.FrameRate", "Frame rate"),
        ("System.Video.EncodingBitrate", "Video bit rate"),
        ("System.Video.Compression", "Video codec"),
        ("System.Audio.Format", "Audio format"),
        ("System.Media.DateEncoded", "Encoded"),
        ("System.Media.Publisher", "Publisher"),
        ("System.GPS.LatitudeDecimal", "Latitude"),
        ("System.GPS.LongitudeDecimal", "Longitude"),
    ];

    /// <summary>Every property name to ask the shell for.</summary>
    public static IReadOnlyList<string> Keys { get; } =
        [.. ImageKeys.Concat(AudioKeys).Concat(DocumentKeys).Concat(OtherKeys).Select(k => k.Key).Distinct(StringComparer.Ordinal)];

    /// <summary>The Image, Audio, Document and "All properties" sections that have at least one value.</summary>
    public static IReadOnlyList<DetailSection> Sections(IReadOnlyDictionary<string, object?> values, string extension)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sections = new List<DetailSection>();
        var shown = new HashSet<string>(StringComparer.Ordinal);

        void Add(string title, (string Key, string Label)[] keys, bool wanted)
        {
            if (!wanted)
            {
                return;
            }

            var rows = Rows(values, keys, shown);
            if (rows.Count > 0)
            {
                sections.Add(new DetailSection(title, rows));
            }
        }

        var ext = extension.ToLowerInvariant();
        Add("Image", ImageKeys, IsImage(ext) || values.ContainsKey("System.Photo.CameraModel") && Has(values, "System.Photo.CameraModel"));
        Add("Audio", AudioKeys, IsAudio(ext) || Has(values, "System.Music.Artist") || Has(values, "System.Music.AlbumTitle"));
        Add("Document", DocumentKeys, IsDocument(ext));

        var rest = Rows(values, OtherKeys, shown);
        if (Gps(values) is { } map)
        {
            rest.Add(new DetailRow("Map", map, Copyable: true));
        }

        if (rest.Count > 0)
        {
            sections.Add(new DetailSection("All properties", rest));
        }

        return sections;
    }

    /// <summary>A link to the place on a map when the photo has GPS, else null.</summary>
    public static string? Gps(IReadOnlyDictionary<string, object?> values) =>
        values.TryGetValue("System.GPS.LatitudeDecimal", out var lat) && values.TryGetValue("System.GPS.LongitudeDecimal", out var lon)
        && AsDouble(lat) is { } la && AsDouble(lon) is { } lo
            ? string.Create(CultureInfo.InvariantCulture, $"https://www.openstreetmap.org/?mlat={la:0.######}&mlon={lo:0.######}#map=15/{la:0.######}/{lo:0.######}")
            : null;

    /// <summary>One value as the pane shows it, or null when it has nothing to say.</summary>
    public static string? Format(string key, object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            case string[] list:
                var joined = string.Join("; ", list.Where(x => !string.IsNullOrWhiteSpace(x)));
                return joined.Length == 0 ? null : joined;
            case DateTimeOffset when value is DateTimeOffset d && d.Year < 1700:
                return null;
            case DateTimeOffset d:
                return DisplayFormat.Date(d);
            case DateTime t:
                return DisplayFormat.Date(new DateTimeOffset(t));
        }

        var number = AsDouble(value);
        if (number is null)
        {
            return value.ToString();
        }

        var n = number.Value;
        return key switch
        {
            "System.Photo.ExposureTime" => n <= 0 ? null : n >= 1 ? $"{n.ToString("0.#", CultureInfo.InvariantCulture)} s" : $"1/{Math.Round(1 / n).ToString("0", CultureInfo.InvariantCulture)} s",
            "System.Photo.FNumber" => $"f/{n.ToString("0.#", CultureInfo.InvariantCulture)}",
            "System.Photo.FocalLength" => $"{n.ToString("0.#", CultureInfo.InvariantCulture)} mm",
            "System.Photo.ISOSpeed" => $"ISO {n.ToString("0", CultureInfo.InvariantCulture)}",
            "System.Photo.Flash" => ((int)n & 1) == 1 ? "Fired" : "Did not fire",
            "System.Photo.Orientation" => Orientation((int)n),
            "System.Image.HorizontalResolution" => $"{n.ToString("0", CultureInfo.InvariantCulture)} dpi",
            "System.Media.Duration" => n <= 0 ? null : FileDetails.Duration(TimeSpan.FromTicks((long)n)),
            "System.Audio.EncodingBitrate" or "System.Video.EncodingBitrate" => n <= 0 ? null : FileDetails.BitRate((long)n),
            "System.Audio.SampleRate" => $"{(n / 1000).ToString("0.#", CultureInfo.InvariantCulture)} kHz",
            "System.Video.FrameRate" => n <= 0 ? null : $"{(n / 1000).ToString("0.###", CultureInfo.InvariantCulture)} fps",
            "System.Rating" => n <= 0 ? null : $"{PropertyEditor.StarsOf(value)} of 5",
            "System.GPS.LatitudeDecimal" or "System.GPS.LongitudeDecimal" => n.ToString("0.000000", CultureInfo.InvariantCulture),
            "System.Music.TrackNumber" or "System.Media.Year" or "System.Document.PageCount" or "System.Document.WordCount" => n <= 0 ? null : n.ToString("0", CultureInfo.InvariantCulture),
            _ => n.ToString("0.###", CultureInfo.InvariantCulture),
        };
    }

    private static List<DetailRow> Rows(IReadOnlyDictionary<string, object?> values, (string Key, string Label)[] keys, HashSet<string> shown)
    {
        var rows = new List<DetailRow>();
        foreach (var (key, label) in keys)
        {
            if (shown.Contains(key) || !values.TryGetValue(key, out var value) || Format(key, value) is not { } text)
            {
                continue;
            }

            shown.Add(key);
            rows.Add(new DetailRow(label, text));
        }

        return rows;
    }

    private static bool Has(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var v) && Format(key, v) is not null;

    private static double? AsDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        uint u => u,
        long l => l,
        ulong ul => ul,
        short sh => sh,
        ushort us => us,
        byte b => b,
        _ => null,
    };

    private static string Orientation(int value) => value switch
    {
        1 => "Normal",
        3 => "Rotated 180°",
        6 => "Rotated 90° clockwise",
        8 => "Rotated 90° counter-clockwise",
        _ => "Flipped or rotated",
    };

    private static bool IsImage(string ext) => ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".heif" or ".avif" or ".tif" or ".tiff" or ".bmp" or ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2";

    private static bool IsAudio(string ext) => ext is ".mp3" or ".flac" or ".m4a" or ".ogg" or ".opus" or ".wav" or ".aac" or ".wma";

    private static bool IsDocument(string ext) => ext is ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".pdf" or ".odt" or ".rtf" or ".txt" or ".md";
}
