using Leviathan.Core.Model;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tags;

/// <summary>
/// Colour tags as the app stores them (spec §11.2): the <c>:leviathan.tags</c> stream travels with the file, and the SQLite index beside
/// it answers "everything with this tag" and holds the tags of locations that cannot keep a stream.
/// <list type="bullet">
/// <item><b>Read:</b> a stream with tags wins and the index is brought up to date from it (so a file moved by another program is
/// found again). With no stream, the index is asked: by identity where there is one (rename or move by another program), else by path
/// with the same size and modified time (FAT, exFAT, SFTP, NFS). A changed file loses its fallback tags rather than keep wrong ones.</item>
/// <item><b>Write:</b> the stream first. Where the location cannot hold one, the tags go to the index alone and the write succeeds;
/// a file that is missing is still an error. Empty tags remove both.</item>
/// </list>
/// All disk work runs off the calling thread. The stream store restores the modified time, so the size and time kept here stay valid.
/// </summary>
public sealed class IndexedTagStore(ITagStore streams, Lazy<ITagIndex> index, IFileIdentitySource identities, Func<string, (long Size, DateTimeOffset Modified)?>? stat = null) : ITagStore
{
    private readonly Func<string, (long Size, DateTimeOffset Modified)?> _stat = stat ?? DiskStat;

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IReadOnlySet<int> onFile;
        try
        {
            onFile = await streams.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            onFile = new HashSet<int>(); // no streams here: the index is all there is
        }

        return await Task.Run(() => Reconcile(path, onFile), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(tags);
        try
        {
            await streams.SetAsync(path, tags, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            // No streams here: the index alone carries the tags (spec §11.2 fallback).
        }

        await Task.Run(() => Record(path, tags), cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlySet<int> Reconcile(string path, IReadOnlySet<int> onFile)
    {
        var stat = _stat(path);
        var identity = identities.Get(path);
        if (onFile.Count > 0)
        {
            Put(path, identity, stat, onFile);
            return onFile;
        }

        var row = Match(path, identity, stat);
        if (row is null)
        {
            return onFile;
        }

        if (!string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            Put(path, identity, stat, row.Tags); // moved by another program: follow it
        }

        // A file that can hold a stream but has none had it stripped (copied by a tool that drops streams); the index still knows.
        return row.Tags;
    }

    private TagIndexEntry? Match(string path, FileIdentity? identity, (long Size, DateTimeOffset Modified)? stat)
    {
        if (identity is { } id && index.Value.FindByIdentity(id) is { } byIdentity)
        {
            return byIdentity;
        }

        if (index.Value.FindByPath(path) is not { } byPath)
        {
            return null;
        }

        // Same identity, or (no identity either side) the same size and modified time to the second.
        if (byPath.Identity is { } stored && identity is { } now)
        {
            return stored == now ? byPath : null;
        }

        return stat is { } s && byPath.Size == s.Size && SameSecond(byPath.Modified, s.Modified) ? byPath : null;
    }

    private void Record(string path, IReadOnlySet<int> tags)
    {
        if (tags.Count == 0)
        {
            index.Value.Remove(path);
            return;
        }

        Put(path, identities.Get(path), _stat(path), tags);
    }

    private void Put(string path, FileIdentity? identity, (long Size, DateTimeOffset Modified)? stat, IReadOnlySet<int> tags) =>
        index.Value.Put(new TagIndexEntry(path, identity, stat?.Size ?? 0, stat?.Modified ?? DateTimeOffset.UnixEpoch, tags));

    private static bool SameSecond(DateTimeOffset a, DateTimeOffset b) => a.UtcTicks / TimeSpan.TicksPerSecond == b.UtcTicks / TimeSpan.TicksPerSecond;

    private static (long Size, DateTimeOffset Modified)? DiskStat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
