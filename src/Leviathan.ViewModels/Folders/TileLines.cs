using Leviathan.Core.Model;

namespace Leviathan.ViewModels.Folders;

/// <summary>
/// The two lines of facts under a name in the Tiles layout (spec §8.1): what the file is (duration, resolution and frame rate for a
/// video, duration, codec and bit rate for a song), then its size and date. Free of UI types so the wording is unit-tested.
/// </summary>
public static class TileLines
{
    /// <summary>The facts line and the size-and-date line of <paramref name="entry"/>; either may be empty.</summary>
    /// <param name="entry">The item.</param>
    /// <param name="media">The tab's media information, or null when there is none.</param>
    public static (string Facts, string Meta) For(FileEntry entry, MediaInfoTracker? media)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (Facts(entry, media), Meta(entry));
    }

    private static string Facts(FileEntry entry, MediaInfoTracker? media)
    {
        if (media is null)
        {
            return string.Empty;
        }

        // "…" while the file waits to be read and "—" when it could not be; both stand for the whole line.
        var first = media.TextFor(entry, MediaColumnKind.Duration);
        if (first is "…" or "—")
        {
            return first;
        }

        var kinds = media.TextFor(entry, MediaColumnKind.Resolution).Length > 0
            ? new[] { MediaColumnKind.Duration, MediaColumnKind.Resolution, MediaColumnKind.FrameRate }
            : [MediaColumnKind.Duration, MediaColumnKind.AudioCodec, MediaColumnKind.Bitrate];
        return string.Join(" · ", kinds.Select(k => media.TextFor(entry, k)).Where(t => t.Length > 0));
    }

    private static string Meta(FileEntry entry)
    {
        var date = DisplayFormat.Date(entry.Modified);
        return entry.Kind == FileEntryKind.File ? $"{DisplayFormat.Size(entry.Size)} · {date}" : date;
    }
}
