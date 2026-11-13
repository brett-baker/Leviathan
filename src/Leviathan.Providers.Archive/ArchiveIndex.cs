using System.IO.Abstractions;
using Leviathan.Core.Archives;

namespace Leviathan.Providers.Archive;

/// <summary>What the archive file held when it was read.</summary>
/// <param name="Entries">Every entry.</param>
/// <param name="Modified">The archive file's own last write time, shown for entries that have none.</param>
public sealed record ArchiveSnapshot(IReadOnlyList<ArchiveEntryInfo> Entries, DateTimeOffset Modified);

/// <summary>
/// Reads an archive's list of entries once and keeps the last few, so opening a folder inside an archive, going up and opening
/// another does not read a 40,000-entry archive again each time (spec §3.1: "a folder opens in under 100 ms"). An entry in the
/// cache is tied to the archive's size and write time, so a changed archive is read afresh. Reads are shared: two tabs opening the
/// same archive wait on one read, and one caller giving up does not stop it for the other.
/// </summary>
public sealed class ArchiveIndex(IArchiveReader reader, IFileSystem fileSystem, int capacity = 4)
{
    private readonly Lock _lock = new();
    private readonly List<(Key Key, Task<ArchiveSnapshot> Read)> _cache = [];

    private sealed record Key(string Path, long Length, long Ticks);

    /// <summary>The reader behind the index, for extracting.</summary>
    public IArchiveReader Reader => reader;

    /// <summary>True when <paramref name="osPath"/> is a file (not a folder that happens to be named like an archive).</summary>
    public bool IsArchiveFile(string osPath) => fileSystem.File.Exists(osPath);

    /// <summary>The contents of the archive at <paramref name="osPath"/>. Throws <see cref="ArchiveException"/> and the usual I/O exceptions.</summary>
    public Task<ArchiveSnapshot> GetAsync(string osPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(osPath);
        var info = fileSystem.FileInfo.New(osPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The archive is not there.", osPath);
        }

        var key = new Key(osPath.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc.Ticks);
        Task<ArchiveSnapshot> read;
        TaskCompletionSource<ArchiveSnapshot>? start = null;
        lock (_lock)
        {
            var hit = _cache.FindIndex(c => c.Key == key);
            if (hit >= 0)
            {
                read = _cache[hit].Read;
                _cache.RemoveAt(hit);
                _cache.Add((key, read));
            }
            else
            {
                // A changed archive leaves its old entry behind; drop every entry for this path.
                _cache.RemoveAll(c => c.Key.Path == key.Path);
                start = new TaskCompletionSource<ArchiveSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                read = start.Task;
                _cache.Add((key, read));
                while (_cache.Count > capacity)
                {
                    _cache.RemoveAt(0);
                }
            }
        }

        if (start is not null)
        {
            _ = FillAsync(start, osPath, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), key);
        }

        return read.WaitAsync(cancellationToken);
    }

    /// <summary>Forgets what is known about the archive at <paramref name="osPath"/>.</summary>
    public void Forget(string osPath)
    {
        var path = osPath.ToUpperInvariant();
        lock (_lock)
        {
            _cache.RemoveAll(c => c.Key.Path == path);
        }
    }

    // Runs the read that a cache entry stands for. The entry goes in before the read starts, so a read that fails at once still
    // removes itself afterwards instead of being left behind.
    private async Task FillAsync(TaskCompletionSource<ArchiveSnapshot> result, string osPath, DateTimeOffset modified, Key key)
    {
        try
        {
            // Not tied to one caller's token: the read is shared, and what it learns is kept for the next caller.
            var entries = await reader.ReadEntriesAsync(osPath, null, CancellationToken.None).ConfigureAwait(false);
            result.SetResult(new ArchiveSnapshot(entries, modified));
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _cache.RemoveAll(c => c.Key == key);
            }

            result.SetException(ex);
        }
    }
}
