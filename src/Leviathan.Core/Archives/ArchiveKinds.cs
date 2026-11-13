using Leviathan.Core.Paths;

namespace Leviathan.Core.Archives;

/// <summary>The part of a location that is an archive file, and the path inside it.</summary>
/// <param name="Archive">The archive file itself (a drive or UNC path).</param>
/// <param name="Inner">Segments below the archive; empty for the archive's own top level.</param>
public sealed record ArchiveSplit(LocationPath Archive, IReadOnlyList<string> Inner)
{
    /// <summary>The path inside the archive with forward slashes and no leading slash; empty at the top.</summary>
    public string InnerPath => string.Join('/', Inner);
}

/// <summary>
/// Which files are browsed as folders (spec §15.4) and how a location splits into "the archive" and "where we are inside it".
/// The split is purely textual so it is cheap and testable; the provider confirms that the archive part is really a file before it
/// believes the split (a folder that happens to be called <c>photos.zip</c> is still a folder).
/// </summary>
public static class ArchiveKinds
{
    private static readonly HashSet<string> Browsable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso", ".cab",
        ".jar", ".apk", ".nupkg", ".vsix", ".cbz", ".cbr",
    };

    /// <summary>True when a file with this extension (with its dot) is browsed as a folder.</summary>
    public static bool IsBrowsable(string extension) => extension.Length > 1 && Browsable.Contains(extension);

    /// <summary>True when the file name has a browsable extension.</summary>
    public static bool IsBrowsableName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && IsBrowsable(name[dot..]);
    }

    /// <summary>
    /// Splits <paramref name="location"/> at the first segment that names an archive. Only drive and share paths can hold archives.
    /// Returns null when no segment does. An archive inside an archive is not opened: the second one is just a file.
    /// </summary>
    public static ArchiveSplit? Split(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Kind is not (LocationKind.Drive or LocationKind.Unc) || (location.Kind == LocationKind.Unc && location.Share is null))
        {
            return null;
        }

        var segments = location.Segments;
        for (var i = 0; i < segments.Count; i++)
        {
            if (!IsBrowsableName(segments[i]))
            {
                continue;
            }

            var archive = location;
            for (var up = segments.Count - 1; up > i; up--)
            {
                archive = archive.Parent!;
            }

            return new ArchiveSplit(archive, [.. segments.Skip(i + 1)]);
        }

        return null;
    }

    /// <summary>True when <paramref name="location"/> is below an archive (not the archive file itself).</summary>
    public static bool IsInside(LocationPath location) => Split(location) is { Inner.Count: > 0 };
}
