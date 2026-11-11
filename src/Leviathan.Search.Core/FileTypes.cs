namespace Leviathan.Search;

/// <summary>Which extensions the <c>type:</c> filter groups together (spec §13.1).</summary>
public static class FileTypes
{
    private static readonly HashSet<string> Video = Set("mkv mp4 avi mov wmv flv webm m4v mpg mpeg ts m2ts mts vob ogv 3gp divx rmvb asf");
    private static readonly HashSet<string> Audio = Set("mp3 flac wav aac m4a ogg opus wma aiff ape alac mka");
    private static readonly HashSet<string> Image = Set("jpg jpeg png gif bmp tif tiff webp heic heif avif svg ico raw cr2 nef arw dng psd");
    private static readonly HashSet<string> Document = Set("pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md csv epub");
    private static readonly HashSet<string> Archive = Set("zip 7z rar tar gz tgz bz2 xz zst cab iso");
    private static readonly HashSet<string> Code = Set("cs js tsx jsx py java c cpp h hpp rs go rb php html css json xml yml yaml sh ps1 sql kt swift");

    private static HashSet<string> Set(string words) => new(words.Split(' '), StringComparer.OrdinalIgnoreCase);

    /// <summary>The extensions (no dot) a kind stands for; empty for folders and plain "file".</summary>
    public static IReadOnlyCollection<string> ExtensionsOf(FileTypeKind kind) => kind switch
    {
        FileTypeKind.Video => Video,
        FileTypeKind.Audio => Audio,
        FileTypeKind.Image => Image,
        FileTypeKind.Document => Document,
        FileTypeKind.Archive => Archive,
        FileTypeKind.Code => Code,
        _ => [],
    };

    /// <summary>True when a file with this extension (no dot) belongs to <paramref name="kind"/>. Folders and plain "file" are decided by the caller.</summary>
    public static bool IsKind(string extensionWithoutDot, FileTypeKind kind) => kind switch
    {
        FileTypeKind.Video => Video.Contains(extensionWithoutDot),
        FileTypeKind.Audio => Audio.Contains(extensionWithoutDot),
        FileTypeKind.Image => Image.Contains(extensionWithoutDot),
        FileTypeKind.Document => Document.Contains(extensionWithoutDot),
        FileTypeKind.Archive => Archive.Contains(extensionWithoutDot),
        FileTypeKind.Code => Code.Contains(extensionWithoutDot),
        _ => false,
    };
}
