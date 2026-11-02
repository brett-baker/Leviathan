using Leviathan.Core.Model;

namespace Leviathan.ViewModels.Views;

/// <summary>Layout of the file view (spec §8.1). Tiles and Gallery arrive with the media data in Phase 3.</summary>
public enum ViewLayout
{
    /// <summary>Rows with configurable columns.</summary>
    Details,

    /// <summary>Compact multi-column names.</summary>
    List,

    /// <summary>Medium thumbnail with two or three lines of metadata.</summary>
    Tiles,

    /// <summary>Thumbnail grid.</summary>
    Thumbnails,

    /// <summary>Large justified thumbnails.</summary>
    Gallery,
}

/// <summary>Sort direction.</summary>
public enum SortDirection
{
    /// <summary>A to Z, small to large, old to new.</summary>
    Ascending,

    /// <summary>The reverse.</summary>
    Descending,
}

/// <summary>Where folders sort relative to files (spec §8.3).</summary>
public enum FolderPlacement
{
    /// <summary>Folders first.</summary>
    First,

    /// <summary>Mixed with files.</summary>
    Mixed,

    /// <summary>Folders last.</summary>
    Last,
}

/// <summary>One sort criterion: a column id and a direction.</summary>
/// <param name="Column">Column id such as <c>name</c> or <c>modified</c>.</param>
/// <param name="Direction">Direction.</param>
public sealed record SortKey(string Column, SortDirection Direction = SortDirection.Ascending);

/// <summary>Layout, sort, grouping and columns of one folder view (spec §8.1–8.3).</summary>
public sealed record FolderViewSettings
{
    /// <summary>The Details columns shown by default, in order.</summary>
    public static IReadOnlyList<string> DefaultColumns { get; } = ["name", "tags", "size", "modified"];

    /// <summary>The settings used where nothing more specific applies.</summary>
    public static FolderViewSettings Default { get; } = new();

    /// <summary>Layout.</summary>
    public ViewLayout Layout { get; init; } = ViewLayout.Details;

    /// <summary>Sort criteria, primary first.</summary>
    public IReadOnlyList<SortKey> Sort { get; init; } = [new SortKey("name")];

    /// <summary>Column id to group by, or null.</summary>
    public string? GroupBy { get; init; }

    /// <summary>Where folders sort.</summary>
    public FolderPlacement Folders { get; init; } = FolderPlacement.First;

    /// <summary>Visible Details columns, in order.</summary>
    public IReadOnlyList<string> Columns { get; init; } = DefaultColumns;

    /// <summary>Widths the user gave columns, by column id; columns not listed use their default width.</summary>
    public IReadOnlyDictionary<string, double> ColumnWidths { get; init; } = new Dictionary<string, double>();

    /// <summary>Edge of a thumbnail in pixels for the Thumbnails layout, 64 to 512 (spec §8.1).</summary>
    public int ThumbnailSize { get; init; } = ViewModels.Folders.ItemFlow.DefaultThumbnailSize;
}

/// <summary>What a folder mostly contains, detected from its listing (spec §8.1).</summary>
public enum FolderKind
{
    /// <summary>No dominant type.</summary>
    General,

    /// <summary>Mostly videos.</summary>
    Videos,

    /// <summary>Mostly pictures.</summary>
    Pictures,

    /// <summary>Mostly music.</summary>
    Music,

    /// <summary>Mostly documents.</summary>
    Documents,
}

/// <summary>Decides a <see cref="FolderKind"/> from the files in a folder.</summary>
public static class FolderKindDetector
{
    /// <summary>Share of files one type must reach to name the folder.</summary>
    public const double DominantShare = 0.6;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv" };

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif", ".bmp", ".tif", ".tiff", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".svg" };

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac" };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".rtf", ".odt", ".ods", ".csv" };

    /// <summary>Returns the dominant kind among the files (folders are ignored), or <see cref="FolderKind.General"/>.</summary>
    public static FolderKind Detect(IEnumerable<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int files = 0, video = 0, picture = 0, music = 0, document = 0;
        foreach (var entry in entries)
        {
            if (entry.Kind != FileEntryKind.File)
            {
                continue;
            }

            files++;
            var extension = entry.Extension;
            if (VideoExtensions.Contains(extension))
            {
                video++;
            }
            else if (PictureExtensions.Contains(extension))
            {
                picture++;
            }
            else if (MusicExtensions.Contains(extension))
            {
                music++;
            }
            else if (DocumentExtensions.Contains(extension))
            {
                document++;
            }
        }

        if (files == 0)
        {
            return FolderKind.General;
        }

        var threshold = files * DominantShare;
        if (video >= threshold)
        {
            return FolderKind.Videos;
        }

        if (picture >= threshold)
        {
            return FolderKind.Pictures;
        }

        if (music >= threshold)
        {
            return FolderKind.Music;
        }

        return document >= threshold ? FolderKind.Documents : FolderKind.General;
    }
}
