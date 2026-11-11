namespace Leviathan.Search;

/// <summary>Whether Everything (voidtools) can answer queries right now (spec §13 "Local index").</summary>
public enum EverythingState
{
    /// <summary>Everything's SDK library was not found, so there is nothing to ask.</summary>
    NotInstalled,

    /// <summary>The library loaded but Everything is not running (or its service cannot be reached).</summary>
    NotRunning,

    /// <summary>Everything is running but has not finished loading its index.</summary>
    Loading,

    /// <summary>Everything answers queries.</summary>
    Ready,
}

/// <summary>One item Everything found.</summary>
/// <param name="FullPath">Full path of the item.</param>
/// <param name="Size">Size in bytes; -1 when Everything does not know it (folders, unindexed sizes).</param>
/// <param name="Modified">Last write time.</param>
/// <param name="Created">Creation time.</param>
/// <param name="Attributes">File-system attributes as Everything reports them.</param>
/// <param name="IsFolder">True for a folder.</param>
public sealed record EverythingHit(string FullPath, long Size, DateTimeOffset Modified, DateTimeOffset Created, FileAttributes Attributes, bool IsFolder);

/// <summary>One page of an Everything answer.</summary>
/// <param name="Hits">The items on this page.</param>
/// <param name="Total">How many items match in all.</param>
public sealed record EverythingPage(IReadOnlyList<EverythingHit> Hits, long Total);

/// <summary>
/// Asks Everything for files by name through its SDK (spec §13, D14): it already indexes every local NTFS volume from the MFT and keeps up with
/// the change journal, so a name search is instant and costs Leviathan no index of its own. The implementation sits in <c>Leviathan.Interop</c>;
/// tests use <c>FakeEverythingClient</c> (spec §19).
/// </summary>
public interface IEverythingClient
{
    /// <summary>Checks whether Everything can answer now. Cheap; call it before choosing a source, not per query.</summary>
    ValueTask<EverythingState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Runs Everything's own search syntax and returns <paramref name="count"/> items from <paramref name="offset"/>.</summary>
    /// <exception cref="InvalidOperationException">Everything is not ready; check <see cref="GetStateAsync"/> first.</exception>
    ValueTask<EverythingPage> QueryAsync(string search, int offset, int count, CancellationToken cancellationToken);
}
