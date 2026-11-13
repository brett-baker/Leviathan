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

    /// <summary>
    /// Unpacks every file of the archive in one pass (spec §15.4). Reading entry by entry would unpack a solid 7z from its start for each
    /// file; one pass reads it once. For each file the reader asks <paramref name="sink"/> where to write it; a null answer skips the file.
    /// Throws <see cref="ArchiveException"/>, and <see cref="OperationCanceledException"/> when the token is cancelled.
    /// </summary>
    Task ExtractAllAsync(string archivePath, string? password, IArchiveExtractionSink sink, CancellationToken cancellationToken);
}

/// <summary>Where one file of an archive is being written during <see cref="IArchiveReader.ExtractAllAsync"/>.</summary>
public interface IExtractionTarget
{
    /// <summary>The stream the reader writes the file's bytes to. The reader may close it when the file is complete.</summary>
    Stream Stream { get; }

    /// <summary>Called once all of the file's bytes were written.</summary>
    void Completed();

    /// <summary>Called when the file could not be unpacked (a wrong password, damaged data); the target removes what it wrote.</summary>
    void Failed(Exception? error);

    /// <summary>
    /// Offers a finished file that 7-Zip already unpacked into <see cref="IArchiveExtractionSink.StagingFolder"/>; the target may take it
    /// by renaming it into place (cheap on the same volume) and then needs no stream. Returns false to have it copied to <see cref="Stream"/>.
    /// </summary>
    bool TryAdopt(string stagedFile) => false;
}

/// <summary>Decides, file by file, where an extraction writes (spec §15.4).</summary>
public interface IArchiveExtractionSink
{
    /// <summary>Returns the target for <paramref name="entry"/>, or null to skip the file. Called in archive order, one file at a time.</summary>
    IExtractionTarget? Open(ArchiveEntryInfo entry);

    /// <summary>
    /// A folder on the volume being written to where a solid archive may be unpacked first (7-Zip cannot hand a solid archive's files out
    /// one at a time). Null means the system temporary folder.
    /// </summary>
    string? StagingFolder => null;

    /// <summary>How far unpacking has got, 0 to 100, while a solid archive is unpacked to <see cref="StagingFolder"/>.</summary>
    void Unpacking(int percent)
    {
    }
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
