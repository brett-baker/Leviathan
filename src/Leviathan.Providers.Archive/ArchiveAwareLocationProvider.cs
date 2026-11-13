using System.Runtime.CompilerServices;
using Leviathan.Core.Archives;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Providers.Archive;

/// <summary>
/// Lets any provider list inside archives (spec §15.4: "browse zip, 7z, rar, tar, gz, iso as if they were folders"). A location
/// below a file with an archive extension is answered from the archive's own list; everything else goes to the provider it wraps, so
/// disks and shares behave exactly as before. A real folder called <c>photos.zip</c> is still a folder: the archive part has to be a
/// file. Archives are read-only here (spec §15.4: "read-only for rar"); changing one is the compress feature's job.
/// </summary>
public sealed class ArchiveAwareLocationProvider(ILocationProvider inner, ArchiveIndex index, Func<LocationPath, string>? toOsPath = null) : ILocationProvider
{
    private readonly Func<LocationPath, string> _toOsPath = toOsPath ?? (l => l.ToWin32Path());

    /// <inheritdoc />
    public string Name => inner.Name;

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) => inner.CanHandle(location);

    /// <inheritdoc />
    public IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(options);
        return ArchiveKinds.Split(location) is { } split ? ListInArchiveAsync(location, split, options, cancellationToken) : inner.ListAsync(location, options, cancellationToken);
    }

    private async IAsyncEnumerable<ListingBatch> ListInArchiveAsync(LocationPath location, ArchiveSplit split, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var archivePath = _toOsPath(split.Archive);
        var isFile = await Task.Run(() => index.IsArchiveFile(archivePath), cancellationToken).ConfigureAwait(false);
        if (!isFile)
        {
            // Not an archive after all: an ordinary folder that has a name like one, or something that is not there.
            await foreach (var batch in inner.ListAsync(location, options, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }

            yield break;
        }

        IReadOnlyList<FileEntry>? children;
        try
        {
            var contents = await index.GetAsync(archivePath, cancellationToken).ConfigureAwait(false);
            children = ArchiveFolder.Children(split.Archive, split.Inner, contents.Entries, contents.Modified);
        }
        catch (ArchiveException ex)
        {
            throw new LocationException(ex.Error is ArchiveError.PasswordRequired or ArchiveError.WrongPassword ? LocationError.AuthenticationRequired : LocationError.Unknown, location, ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw LocationErrors.Wrap(ex, location);
        }

        if (children is null)
        {
            throw new LocationException(LocationError.NotFound, location, $"{location.Name} is not in {split.Archive.Name}.");
        }

        var included = children.Where(options.Includes).ToList();
        for (var i = 0; i < included.Count; i += options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(options.BatchSize, included.Count - i);
            yield return new ListingBatch(included.GetRange(i, count), i + count >= included.Count);
        }

        if (included.Count == 0)
        {
            yield return new ListingBatch([], true);
        }
    }
}
