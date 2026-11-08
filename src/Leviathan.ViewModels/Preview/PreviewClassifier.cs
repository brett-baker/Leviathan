using Leviathan.Core.Model;

namespace Leviathan.ViewModels.Preview;

/// <summary>What the preview pane shows for a selection (spec §9.1).</summary>
public enum PreviewKind
{
    /// <summary>Nothing is selected.</summary>
    None,

    /// <summary>A video, played by the engine.</summary>
    Video,

    /// <summary>An audio file, played by the engine.</summary>
    Audio,

    /// <summary>A picture.</summary>
    Image,

    /// <summary>A PDF.</summary>
    Pdf,

    /// <summary>Plain text or source code.</summary>
    Text,

    /// <summary>Markdown, rendered.</summary>
    Markdown,

    /// <summary>HTML, rendered with scripts and network off.</summary>
    Html,

    /// <summary>An Office document, shown by its registered shell preview handler.</summary>
    Office,

    /// <summary>An archive: the list of what is inside.</summary>
    Archive,

    /// <summary>A font: sample text at several sizes.</summary>
    Font,

    /// <summary>A folder: count, size and a small space map.</summary>
    Folder,

    /// <summary>More than one item: count, size, type breakdown and a strip of thumbnails.</summary>
    Multiple,

    /// <summary>A file with no preview of its own; the pane shows its name and details.</summary>
    Other,
}

/// <summary>The limits that keep a preview from stalling the pane (spec §9.1: "capped by size and time").</summary>
public static class PreviewLimits
{
    /// <summary>How long a selection must rest before its preview loads, so arrowing through files stays fast.</summary>
    public static readonly TimeSpan RestDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How much of a text file is read for the preview.</summary>
    public const long MaxTextBytes = 2L << 20;

    /// <summary>The largest picture file read for the pane (256 MiB); a bigger one shows its details instead, so one file never stalls the pane or fills memory.</summary>
    public const long MaxImageBytes = 256L << 20;

    /// <summary>The widest a picture is decoded for the pane; larger pictures are decoded smaller, which keeps memory bounded while zoom still reaches useful detail.</summary>
    public const int MaxDecodeWidth = 6000;

    /// <summary>How long a renderer may take before the pane gives up and shows the file's details instead.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Decides which renderer a selection gets (spec §9.1). By extension only, because opening each file to find out would defeat the
/// point of a quick preview; a renderer that finds the file is not what its name says shows the details instead. Pure, so the table is
/// tested and the renderers added in later slices only have to agree with it.
/// </summary>
public static class PreviewClassifier
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".webp", ".bmp", ".dib", ".ico", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl", ".svg", ".psd",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".srw", ".pef",
    };

    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md5", ".sha1", ".sha256", ".nfo", ".srt", ".vtt", ".ass", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".env",
        ".cs", ".csx", ".vb", ".fs", ".cpp", ".cc", ".c", ".h", ".hpp", ".java", ".kt", ".go", ".rs", ".py", ".rb", ".php", ".pl", ".lua", ".swift", ".dart",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".less", ".sql", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".gitignore", ".gitattributes",
        ".csproj", ".vbproj", ".fsproj", ".sln", ".slnx", ".props", ".targets", ".xaml", ".resx", ".config", ".editorconfig", ".rtf",
    };

    private static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".dot", ".dotx", ".docm", ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odt", ".ods", ".odp", ".vsd", ".vsdx", ".msg", ".eml",
    };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst", ".cab", ".iso", ".wim", ".jar", ".apk", ".nupkg", ".vsix",
    };

    private static readonly HashSet<string> Fonts = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc", ".woff", ".woff2", ".fon" };

    /// <summary>The preview a selection gets.</summary>
    public static PreviewKind Classify(IReadOnlyList<FileEntry> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Count switch
        {
            0 => PreviewKind.None,
            1 => Classify(selection[0]),
            _ => PreviewKind.Multiple,
        };
    }

    /// <summary>The preview one entry gets.</summary>
    public static PreviewKind Classify(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != FileEntryKind.File)
        {
            return PreviewKind.Folder;
        }

        var extension = entry.Extension;
        return extension switch
        {
            _ when Leviathan.Media.MediaFileKinds.IsVideo(extension) => PreviewKind.Video,
            _ when Leviathan.Media.MediaFileKinds.IsAudio(extension) => PreviewKind.Audio,
            _ when Images.Contains(extension) => PreviewKind.Image,
            ".pdf" => PreviewKind.Pdf,
            ".md" or ".markdown" or ".mdown" => PreviewKind.Markdown,
            ".html" or ".htm" or ".xhtml" => PreviewKind.Html,
            _ when Office.Contains(extension) => PreviewKind.Office,
            _ when Archives.Contains(extension) => PreviewKind.Archive,
            _ when Fonts.Contains(extension) => PreviewKind.Font,
            _ when Text.Contains(extension) => PreviewKind.Text,
            _ => PreviewKind.Other,
        };
    }
}
