using Leviathan.Core.Archives;

namespace Leviathan.Providers.Archive;

/// <summary>Picks the reader for each archive: tar and tar.gz through the framework, everything else through 7-Zip (spec §15.4).</summary>
public sealed class CompositeArchiveReader(IArchiveReader tar, IArchiveReader general) : IArchiveReader
{
    /// <summary>The readers the app uses.</summary>
    public static CompositeArchiveReader Default() => new(new TarArchiveReader(), new SevenZipArchiveReader());

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchiveEntryInfo>> ReadEntriesAsync(string archivePath, string? password, CancellationToken cancellationToken) =>
        For(archivePath).ReadEntriesAsync(archivePath, password, cancellationToken);

    /// <inheritdoc />
    public Task ExtractEntryAsync(string archivePath, string entryPath, Stream destination, string? password, CancellationToken cancellationToken) =>
        For(archivePath).ExtractEntryAsync(archivePath, entryPath, destination, password, cancellationToken);

    private IArchiveReader For(string archivePath) => TarArchiveReader.Handles(archivePath) ? tar : general;
}
