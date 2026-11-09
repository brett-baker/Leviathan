using Leviathan.Core.Model;

namespace Leviathan.ViewModels.Tags;

/// <summary>One row of the tag index: where a file is, what identifies it and which tags it has (spec §11.2).</summary>
/// <param name="Path">Full path when last seen.</param>
/// <param name="Identity">Volume serial and file ID on NTFS, or null where the file system gives none (FAT, exFAT, SFTP, NFS).</param>
/// <param name="Size">Size in bytes when last seen; part of the key where there is no identity.</param>
/// <param name="Modified">Modified time when last seen; part of the key where there is no identity.</param>
/// <param name="Tags">Tag ids; never empty in a stored row.</param>
public sealed record TagIndexEntry(string Path, FileIdentity? Identity, long Size, DateTimeOffset Modified, IReadOnlySet<int> Tags);

/// <summary>
/// The local index of tagged files (spec §11.2, D11): finds "everything with this tag" without reading a stream from every file, and
/// carries the tags of locations that cannot hold a stream. Rows are found by path, or by identity when a file was moved by another
/// program. Implementations are thread-safe.
/// </summary>
public interface ITagIndex
{
    /// <summary>The row for this path, or null.</summary>
    TagIndexEntry? FindByPath(string path);

    /// <summary>The row for this file identity, or null.</summary>
    TagIndexEntry? FindByIdentity(FileIdentity identity);

    /// <summary>Stores the row for the entry's path. A row with no tags removes it; any other row with the same identity (the file's old path) is dropped.</summary>
    void Put(TagIndexEntry entry);

    /// <summary>Removes the row for this path.</summary>
    void Remove(string path);

    /// <summary>The paths of every indexed file with this tag, for the Tags section of the sidebar.</summary>
    IReadOnlyList<string> PathsWithTag(int tagId);
}
