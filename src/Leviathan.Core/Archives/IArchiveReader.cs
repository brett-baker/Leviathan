using Leviathan.Core.Paths;

namespace Leviathan.Core.Archives;

/// <summary>
/// Reads archives (spec §15.4, §18: 7-Zip behind a small interface so the logic around it is tested with a fake). Calls may take a
/// while on a big archive or a slow share, so they are async and honour the token; nothing here may run on the UI thread.
/// </summary>
public interface IArchiveReader
{
    /// <summary>Every entry of the archive at <paramref name="archivePath"/> (an OS path). Throws <see cref="ArchiveException"/>.</summary>
    Task<IReadOnlyList<ArchiveEntryInfo>> ReadEntriesAsync(string archivePath, string? password, CancellationToken cancellationToken);

    /// <summary>Writes the unpacked bytes of one entry to <paramref name="destination"/>. Throws <see cref="ArchiveException"/>.</summary>
    Task ExtractEntryAsync(string archivePath, string entryPath, Stream destination, string? password, CancellationToken cancellationToken);
}

/// <summary>
/// Makes a real file of something inside an archive so programs that only know paths (the default app, the preview renderers) can
/// use it. The file lives in a scratch folder and is cleaned up later; it is never the user's data.
/// </summary>
public interface IArchiveStager
{
    /// <summary>
    /// Returns an OS path with the contents of <paramref name="entry"/>. A location that is not really inside an archive is returned
    /// as its own OS path, unchanged. Throws <see cref="ArchiveException"/> when the entry cannot be unpacked.
    /// </summary>
    Task<string> StageAsync(LocationPath entry, CancellationToken cancellationToken);
}
