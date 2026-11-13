using Leviathan.Contracts;
using SevenZip;

namespace Leviathan.Providers.Archive;

/// <summary>One file to put in an archive.</summary>
/// <param name="NameInArchive">Path inside the archive, with forward slashes.</param>
/// <param name="SourcePath">The file on disk.</param>
/// <param name="Size">Its size, for progress.</param>
public sealed record ArchiveWriteEntry(string NameInArchive, string SourcePath, long Size);

/// <summary>
/// Creates zip and 7z archives (spec §15.4, §18: 7-Zip behind a small interface so the job around it is tested with a fake).
/// Runs on a pool thread; the caller owns the file it names and the clean-up after a failure.
/// </summary>
public interface IArchiveWriter
{
    /// <summary>
    /// Writes <paramref name="entries"/> to a new archive at <paramref name="archivePath"/>. <paramref name="progress"/> is told how many
    /// bytes of the entries are done and which entry is next. Throws <see cref="OperationCanceledException"/> when cancelled and
    /// <see cref="Leviathan.Core.Archives.ArchiveException"/> when the archive could not be made.
    /// </summary>
    Task WriteAsync(string archivePath, ArchiveFormat format, ArchiveLevel level, string? password, IReadOnlyList<ArchiveWriteEntry> entries, Action<long, string>? progress, CancellationToken cancellationToken);
}

/// <summary>Writes archives through 7-Zip (spec §15.4). A password means AES-256 for zip, and for 7z an encrypted list of files too.</summary>
public sealed class SevenZipArchiveWriter : IArchiveWriter
{
    /// <inheritdoc />
    public Task WriteAsync(string archivePath, ArchiveFormat format, ArchiveLevel level, string? password, IReadOnlyList<ArchiveWriteEntry> entries, Action<long, string>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArgumentNullException.ThrowIfNull(entries);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SevenZipArchiveReader.EnsureLibrary();
                var compressor = new SevenZipCompressor
                {
                    ArchiveFormat = format == ArchiveFormat.SevenZip ? OutArchiveFormat.SevenZip : OutArchiveFormat.Zip,
                    CompressionLevel = level switch
                    {
                        ArchiveLevel.Store => CompressionLevel.None,
                        ArchiveLevel.Fastest or ArchiveLevel.Fast => CompressionLevel.Fast,
                        ArchiveLevel.Normal => CompressionLevel.Normal,
                        ArchiveLevel.Maximum => CompressionLevel.High,
                        _ => CompressionLevel.Ultra,
                    },
                    DirectoryStructure = true,
                    PreserveDirectoryRoot = false,
                    EventSynchronization = EventSynchronizationStrategy.AlwaysSynchronous,
                };
                if (!string.IsNullOrEmpty(password))
                {
                    compressor.ZipEncryptionMethod = ZipEncryptionMethod.Aes256;
                    compressor.EncryptHeaders = format == ArchiveFormat.SevenZip;
                }

                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    files[entry.NameInArchive] = entry.SourcePath;
                    sizes[entry.SourcePath] = entry.Size;
                    sizes[entry.NameInArchive] = entry.Size;
                }

                long done = 0;
                long last = 0;
                compressor.FileCompressionStarted += (_, e) =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        e.Cancel = true;
                        return;
                    }

                    done += last;
                    last = sizes.GetValueOrDefault(e.FileName);
                    progress?.Invoke(done, e.FileName);
                };

                try
                {
                    compressor.CompressFileDictionary(files, archivePath, password ?? string.Empty);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or Leviathan.Core.Archives.ArchiveException))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ex is IOException or UnauthorizedAccessException)
                    {
                        throw;
                    }

                    throw new Leviathan.Core.Archives.ArchiveException(Leviathan.Core.Archives.ArchiveError.Unreadable, $"{Path.GetFileName(archivePath)} could not be created: {ex.Message}", ex);
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(done + last, string.Empty);
            },
            cancellationToken);
    }
}
