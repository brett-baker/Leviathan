using System.Formats.Tar;
using System.IO.Compression;
using Leviathan.Core.Archives;

namespace Leviathan.Providers.Archive;

/// <summary>
/// Reads <c>.tar</c>, <c>.tgz</c> and <c>.tar.gz</c> with the framework's own tar reader (spec §15.4). 7-Zip sees a <c>.tar.gz</c> as
/// a gzip holding one tar, which would list a single "x.tar" instead of the files; reading the pair as one archive gives the files.
/// A tar is read from the front, so extracting one entry reads up to it.
/// </summary>
public sealed class TarArchiveReader : IArchiveReader
{
    /// <summary>True when this reader handles the file (by its name).</summary>
    public static bool Handles(string path) =>
        path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchiveEntryInfo>> ReadEntriesAsync(string archivePath, string? password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        return Task.Run<IReadOnlyList<ArchiveEntryInfo>>(
            () =>
            {
                var entries = new List<ArchiveEntryInfo>();
                Walk(archivePath, entry =>
                {
                    entries.Add(Describe(entry));
                    return true;
                }, cancellationToken);
                return entries;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ExtractEntryAsync(string archivePath, string entryPath, Stream destination, string? password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArgumentException.ThrowIfNullOrEmpty(entryPath);
        ArgumentNullException.ThrowIfNull(destination);
        return Task.Run(
            () =>
            {
                var wanted = ArchiveEntryInfo.NormalizePath(entryPath);
                var found = false;
                Walk(archivePath, entry =>
                {
                    var info = Describe(entry);
                    if (info.IsDirectory || !info.Path.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    found = true;
                    entry.DataStream?.CopyTo(destination);
                    return false;
                }, cancellationToken);
                if (!found)
                {
                    throw new ArchiveException(ArchiveError.EntryNotFound, $"{entryPath} is not in {Path.GetFileName(archivePath)}.");
                }
            },
            cancellationToken);
    }

    private static ArchiveEntryInfo Describe(TarEntry entry)
    {
        var path = ArchiveEntryInfo.NormalizePath(entry.Name);
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        path = path.TrimStart('.', '/');
        var isDirectory = entry.EntryType == TarEntryType.Directory;
        return new ArchiveEntryInfo(path, isDirectory, isDirectory ? 0 : entry.Length, entry.ModificationTime, null);
    }

    // Calls visit for each real entry (pax headers, links and devices are skipped) until it returns false.
    private static void Walk(string archivePath, Func<TarEntry, bool> visit, CancellationToken cancellationToken)
    {
        try
        {
            using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            var gzip = !archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
            using var stream = gzip ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
            using var tar = new TarReader(stream, leaveOpen: true);
            while (tar.GetNextEntry(copyData: false) is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes or TarEntryType.SymbolicLink
                    or TarEntryType.HardLink or TarEntryType.CharacterDevice or TarEntryType.BlockDevice or TarEntryType.Fifo)
                {
                    continue;
                }

                if (!visit(entry))
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or InvalidOperationException)
        {
            throw new ArchiveException(ArchiveError.Unreadable, $"{Path.GetFileName(archivePath)} could not be read as a tar archive: {ex.Message}", ex);
        }
    }
}
