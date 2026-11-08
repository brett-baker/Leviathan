using System.Formats.Tar;
using System.IO.Compression;

namespace Leviathan.ViewModels.Preview;

/// <summary>One item inside an archive.</summary>
public sealed record ArchiveItem(string Path, long Size, bool IsFolder);

/// <summary>The inside of an archive, ready to show; <paramref name="Truncated"/> says the list stopped at <see cref="ArchiveListing.MaxItems"/>.</summary>
public sealed record ArchiveContents(IReadOnlyList<ArchiveItem> Items, long TotalBytes, bool Truncated);

/// <summary>
/// Lists what is inside an archive without extracting it (spec §9.1: "file list inside the archive"). ZIP and the formats built on it
/// (JAR, APK, NUPKG, VSIX), TAR and TAR.GZ are read with the framework's own readers, which only read the directory of the archive, not the
/// data. 7z, RAR and the rest need the 7-Zip library of the archive slice (spec §18) and are reported as unsupported here.
/// </summary>
public static class ArchiveListing
{
    /// <summary>How many items are listed; a bigger archive shows the first ones and says so.</summary>
    public const int MaxItems = 2000;

    /// <summary>True when <paramref name="extension"/> (with its dot) is a kind this class can list.</summary>
    public static bool CanList(string extension) => extension.ToLowerInvariant() is ".zip" or ".jar" or ".apk" or ".nupkg" or ".vsix" or ".tar" or ".tgz" or ".gz";

    /// <summary>Lists a ZIP-format stream (must be seekable).</summary>
    public static ArchiveContents ReadZip(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var items = new List<ArchiveItem>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var isFolder = entry.FullName.EndsWith('/');
            total += entry.Length;
            if (items.Count < MaxItems)
            {
                items.Add(new ArchiveItem(entry.FullName.TrimEnd('/'), entry.Length, isFolder));
            }
        }

        return new ArchiveContents(items, total, zip.Entries.Count > MaxItems);
    }

    /// <summary>Lists a TAR stream; <paramref name="gzip"/> says it is compressed with gzip.</summary>
    public static ArchiveContents ReadTar(Stream stream, bool gzip)
    {
        using var inner = gzip ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true) : stream;
        using var tar = new TarReader(inner, leaveOpen: true);
        var items = new List<ArchiveItem>();
        long total = 0;
        var count = 0;
        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            count++;
            total += entry.Length;
            if (items.Count < MaxItems)
            {
                items.Add(new ArchiveItem(entry.Name.TrimEnd('/'), entry.Length, entry.EntryType == TarEntryType.Directory));
            }
        }

        return new ArchiveContents(items, total, count > MaxItems);
    }

    /// <summary>Lists the archive at <paramref name="path"/> by its extension; throws <see cref="NotSupportedException"/> for kinds this class cannot read and <see cref="InvalidDataException"/> for a damaged archive.</summary>
    public static ArchiveContents Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".tar" => ReadTar(file, gzip: false),
                ".tgz" => ReadTar(file, gzip: true),
                ".gz" when path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) => ReadTar(file, gzip: true),
                ".gz" => throw new NotSupportedException("A single gzip file has no list of files."),
                ".zip" or ".jar" or ".apk" or ".nupkg" or ".vsix" => ReadZip(file),
                _ => throw new NotSupportedException($"Listing {extension} archives needs 7-Zip support, which comes with the archive feature."),
            };
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The archive is damaged or not in the format its name says.", ex);
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or IOException && ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new InvalidDataException("The archive is damaged or not in the format its name says.", ex);
        }
    }
}
