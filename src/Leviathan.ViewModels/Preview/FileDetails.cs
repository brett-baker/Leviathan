using System.Globalization;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Media;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Preview;

/// <summary>One label and value in the details pane.</summary>
/// <param name="Label">What it is ("Size").</param>
/// <param name="Value">The text.</param>
/// <param name="Copyable">True when clicking the value copies it (the path).</param>
public sealed record DetailRow(string Label, string Value, bool Copyable = false);

/// <summary>A titled, collapsible group of rows in the details pane.</summary>
/// <param name="Title">"General", "Media", ….</param>
/// <param name="Rows">The rows.</param>
/// <param name="Summary">An optional line shown above the rows (the media summary).</param>
public sealed record DetailSection(string Title, IReadOnlyList<DetailRow> Rows, string? Summary = null);

/// <summary>
/// What the details pane shows for one file (spec §9.2): the General group from the listing, and the Media group from ffprobe's result
/// as a summary line plus one row per stream. Pure text, no controls, so wording and rounding are tested without the window.
/// </summary>
public static class FileDetails
{
    /// <summary>General: name, path, type, size, dates, attributes.</summary>
    public static DetailSection General(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var rows = new List<DetailRow>
        {
            new("Name", entry.Name),
            new("Path", entry.Location.ToWin32Path(), Copyable: true),
            new("Type", entry.Kind == FileEntryKind.File ? (entry.Extension.Length > 0 ? entry.Extension.TrimStart('.').ToUpperInvariant() + " file" : "File") : "Folder"),
        };
        if (entry.Kind == FileEntryKind.File)
        {
            rows.Add(new("Size", $"{DisplayFormat.Size(entry.Size)} ({entry.Size.ToString("N0", CultureInfo.CurrentCulture)} bytes)"));
        }

        rows.Add(new("Modified", DisplayFormat.Date(entry.Modified)));
        rows.Add(new("Created", DisplayFormat.Date(entry.Created)));
        var attributes = Attributes(entry.Attributes);
        if (attributes.Length > 0)
        {
            rows.Add(new("Attributes", attributes));
        }

        return new DetailSection("General", rows);
    }

    /// <summary>"Read-only, Hidden" for the attributes worth showing.</summary>
    public static string Attributes(FileAttributes attributes)
    {
        var names = new List<string>();
        void Add(FileAttributes flag, string name)
        {
            if (attributes.HasFlag(flag))
            {
                names.Add(name);
            }
        }

        Add(FileAttributes.ReadOnly, "Read-only");
        Add(FileAttributes.Hidden, "Hidden");
        Add(FileAttributes.System, "System");
        Add(FileAttributes.Archive, "Archive");
        Add(FileAttributes.Compressed, "Compressed");
        Add(FileAttributes.Encrypted, "Encrypted");
        Add(FileAttributes.ReparsePoint, "Link");
        Add(FileAttributes.Offline, "Offline");
        return string.Join(", ", names);
    }

    /// <summary>"1h 42m · 3840×2160 · HEVC 10-bit HDR10 · 23.976 fps · 18.4 Mb/s" (spec §9.2); parts the file does not state are left out.</summary>
    public static string MediaSummary(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var parts = new List<string>();
        if (info.Duration is { } duration)
        {
            parts.Add(Duration(duration));
        }

        if (info.PrimaryVideo is { } v)
        {
            if (v.Width is { } w && v.Height is { } h)
            {
                parts.Add($"{w}×{h}");
            }

            var codec = string.Join(' ', new[] { v.Codec?.ToUpperInvariant(), v.BitDepth is { } depth and > 8 ? $"{depth}-bit" : null, HdrName(v.Hdr) }.OfType<string>());
            if (codec.Length > 0)
            {
                parts.Add(codec);
            }

            if (v.FrameRate is { } fps and > 0)
            {
                parts.Add($"{fps.ToString("0.###", CultureInfo.InvariantCulture)} fps");
            }
        }

        if (info.BitRate is { } rate and > 0)
        {
            parts.Add(BitRate(rate));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The Media group: the summary, then a row per video, audio and subtitle stream, the chapter count and the container.</summary>
    public static DetailSection Media(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var rows = new List<DetailRow>();
        if (info.Container is { Length: > 0 } container)
        {
            rows.Add(new("Container", container));
        }

        foreach (var v in info.Video.Where(v => !v.IsCoverArt))
        {
            var size = v.Width is { } w && v.Height is { } h ? $"{w}×{h}" : null;
            rows.Add(new($"Video {v.Index}", Join(v.Codec?.ToUpperInvariant(), v.Profile, size, v.FrameRate is > 0 ? $"{v.FrameRate.Value.ToString("0.###", CultureInfo.InvariantCulture)} fps" : null, v.PixelFormat, v.BitDepth is { } d ? $"{d}-bit" : null, HdrName(v.Hdr), v.BitRate is > 0 ? BitRate(v.BitRate.Value) : null, v.Title)));
        }

        foreach (var a in info.Audio)
        {
            rows.Add(new($"Audio {a.Index}", Join(a.Language, a.Codec?.ToUpperInvariant(), a.ChannelLayout ?? (a.Channels is { } c ? $"{c} ch" : null), a.SampleRate is { } hz ? $"{hz / 1000.0:0.#} kHz" : null, a.BitRate is > 0 ? BitRate(a.BitRate.Value) : null, a.Title, a.IsDefault ? "default" : null)));
        }

        foreach (var s in info.Subtitles)
        {
            rows.Add(new($"Subtitle {s.Index}", Join(s.Language, s.Codec, s.Title, s.IsForced ? "forced" : null, s.IsDefault ? "default" : null)));
        }

        if (info.Chapters.Count > 0)
        {
            rows.Add(new("Chapters", info.Chapters.Count.ToString(CultureInfo.CurrentCulture)));
        }

        foreach (var tag in info.Tags.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new(tag.Key, tag.Value));
        }

        return new DetailSection("Media", rows, MediaSummary(info));
    }

    /// <summary>"1h 42m", "7m 05s", "42s".</summary>
    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
        : duration.TotalMinutes >= 1 ? $"{duration.Minutes}m {duration.Seconds:00}s"
        : $"{Math.Max(0, (int)duration.TotalSeconds)}s";

    /// <summary>"18.4 Mb/s", "640 kb/s".</summary>
    public static string BitRate(long bitsPerSecond) =>
        bitsPerSecond >= 1_000_000 ? $"{bitsPerSecond / 1_000_000.0:0.#} Mb/s" : $"{bitsPerSecond / 1000.0:0} kb/s";

    private static string? HdrName(HdrKind kind) => kind switch
    {
        HdrKind.Hdr10 => "HDR10",
        HdrKind.Hlg => "HLG",
        HdrKind.DolbyVision => "Dolby Vision",
        _ => null,
    };

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
