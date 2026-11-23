using Leviathan.Core.Paths;

namespace Leviathan.Core.Images;

/// <summary>What an image edit does (spec §6.2: rotate, convert; the rest of the Image submenu).</summary>
public enum ImageEditKind
{
    /// <summary>Turn a quarter turn anticlockwise.</summary>
    RotateLeft,

    /// <summary>Turn a quarter turn clockwise.</summary>
    RotateRight,

    /// <summary>Mirror left to right.</summary>
    FlipHorizontal,

    /// <summary>Mirror top to bottom.</summary>
    FlipVertical,

    /// <summary>Scale to a percentage of the original size.</summary>
    Resize,

    /// <summary>Save in another format.</summary>
    Convert,
}

/// <summary>A format an image can be saved in.</summary>
public enum ImageFormat
{
    /// <summary>PNG.</summary>
    Png,

    /// <summary>JPEG.</summary>
    Jpeg,

    /// <summary>Bitmap.</summary>
    Bmp,

    /// <summary>GIF (a single frame).</summary>
    Gif,

    /// <summary>TIFF.</summary>
    Tiff,
}

/// <summary>One edit of an image.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Percent">For <see cref="ImageEditKind.Resize"/>: the new size as a percentage of the old, 1 to 400.</param>
/// <param name="Format">For <see cref="ImageEditKind.Convert"/>: the format to save in. Otherwise null: the format the picture is in, else PNG.</param>
public sealed record ImageEdit(ImageEditKind Kind, int Percent = 100, ImageFormat? Format = null);

/// <summary>An image edit failed (a file the imaging component could not read or write).</summary>
public sealed class ImageEditException : IOException
{
    /// <summary>Creates the exception.</summary>
    public ImageEditException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Edits pictures through the Windows imaging component (spec §6.2). Every edit saves a new file beside the original and never changes
/// the original, so nothing is lost and there is nothing to undo.
/// </summary>
public interface IImageEditor
{
    /// <summary>Applies <paramref name="edit"/> to <paramref name="source"/>, writes the result next to it under a new name, and returns that name.</summary>
    /// <exception cref="ImageEditException">The picture could not be read or written.</exception>
    Task<LocationPath> EditAsync(LocationPath source, ImageEdit edit, CancellationToken cancellationToken);
}

/// <summary>The rules for which files can be edited and what the result is called. Pure, so they are tested without the imaging component.</summary>
public static class ImageEditRules
{
    private static readonly Dictionary<string, ImageFormat> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = ImageFormat.Png,
        [".jpg"] = ImageFormat.Jpeg,
        [".jpeg"] = ImageFormat.Jpeg,
        [".bmp"] = ImageFormat.Bmp,
        [".gif"] = ImageFormat.Gif,
        [".tif"] = ImageFormat.Tiff,
        [".tiff"] = ImageFormat.Tiff,
    };

    /// <summary>Pictures Windows can read on every machine but cannot write in their own format; an edit of one is saved as PNG.</summary>
    private static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase) { ".webp", ".heic", ".heif", ".avif" };

    /// <summary>True when the file with this extension is a picture Leviathan edits.</summary>
    public static bool CanEdit(string? extension) => extension is not null && (Formats.ContainsKey(extension) || ReadOnly.Contains(extension));

    /// <summary>The format an edit of a picture with this extension is saved in when the edit does not name one.</summary>
    public static ImageFormat FormatOf(string extension) => Formats.GetValueOrDefault(extension, ImageFormat.Png);

    /// <summary>The file extension of a format, with the dot.</summary>
    public static string ExtensionOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Gif => ".gif",
        ImageFormat.Tiff => ".tif",
        _ => ".png",
    };

    /// <summary>The text added to the name: <c>photo (rotated left).jpg</c>. A conversion adds nothing; the extension says it.</summary>
    public static string Suffix(ImageEdit edit) => edit.Kind switch
    {
        ImageEditKind.RotateLeft => " (rotated left)",
        ImageEditKind.RotateRight => " (rotated right)",
        ImageEditKind.FlipHorizontal => " (flipped horizontally)",
        ImageEditKind.FlipVertical => " (flipped vertically)",
        ImageEditKind.Resize => $" ({edit.Percent}%)",
        _ => string.Empty,
    };

    /// <summary>
    /// The name of the file an edit of <paramref name="sourceName"/> is saved as, numbered ("photo (rotated left) (2).jpg") while
    /// <paramref name="exists"/> says the name is taken. The original is never a candidate.
    /// </summary>
    public static string OutputName(string sourceName, ImageEdit edit, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(exists);
        var stem = Path.GetFileNameWithoutExtension(sourceName);
        var extension = ExtensionOf(edit.Format ?? FormatOf(Path.GetExtension(sourceName)));
        var first = stem + Suffix(edit);
        var name = first + extension;
        for (var n = 2; exists(name) || string.Equals(name, sourceName, StringComparison.OrdinalIgnoreCase); n++)
        {
            name = $"{first} ({n}){extension}";
        }

        return name;
    }

    /// <summary>The words for the status bar: "Rotated left", "Resized to 50%".</summary>
    public static string Verb(ImageEdit edit) => edit.Kind switch
    {
        ImageEditKind.RotateLeft => "Rotated left",
        ImageEditKind.RotateRight => "Rotated right",
        ImageEditKind.FlipHorizontal => "Flipped horizontally",
        ImageEditKind.FlipVertical => "Flipped vertically",
        ImageEditKind.Resize => $"Resized to {edit.Percent}%",
        _ => $"Converted to {(edit.Format ?? ImageFormat.Png).ToString().ToUpperInvariant()}",
    };
}
