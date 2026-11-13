using Leviathan.Core.Archives;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Providers.Archive;

/// <summary>
/// Tells the address bar what a path inside an archive is, from the same cached list the folder listing uses (spec §5.2, §15.4), so
/// typing <c>D:\Backups\site.zip\images</c> goes to that folder instead of "not found".
/// </summary>
public sealed class ArchiveCatalog(ArchiveIndex index, Func<LocationPath, string>? toOsPath = null) : IArchiveCatalog
{
    private readonly Func<LocationPath, string> _toOsPath = toOsPath ?? (l => l.ToWin32Path());

    /// <inheritdoc />
    public async Task<ArchiveProbe> ProbeAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (ArchiveKinds.Split(location) is not { Inner.Count: > 0 } split)
        {
            return ArchiveProbe.NotInArchive;
        }

        var archivePath = _toOsPath(split.Archive);
        if (!await Task.Run(() => index.IsArchiveFile(archivePath), cancellationToken).ConfigureAwait(false))
        {
            return ArchiveProbe.NotInArchive;
        }

        var contents = await index.GetAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (ArchiveFolder.Find(contents.Entries, split.InnerPath) is not null)
        {
            return new ArchiveProbe(true, FileEntryKind.File);
        }

        return new ArchiveProbe(true, ArchiveFolder.Children(split.Archive, split.Inner, contents.Entries, contents.Modified) is null ? null : FileEntryKind.Folder);
    }
}
