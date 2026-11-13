using System.Text;
using Leviathan.Core.Archives;

namespace Leviathan.Testing;

/// <summary>
/// An archive reader over an in-memory list of entries, with counters and switches for the cases tests care about: a slow read, a
/// failure, how often the archive was read. Add archives with <see cref="Add"/>; file entries may carry text content.
/// </summary>
public sealed class FakeArchiveReader : IArchiveReader
{
    private readonly Dictionary<string, List<(ArchiveEntryInfo Info, byte[] Content)>> _archives = new(StringComparer.OrdinalIgnoreCase);
    private int _reads;
    private int _extractions;

    /// <summary>How many times the entry list was read.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>How many entries were extracted.</summary>
    public int Extractions => Volatile.Read(ref _extractions);

    /// <summary>When set, reads wait for it to complete (a slow archive).</summary>
    public Task? ReadGate { get; set; }

    /// <summary>When set, reads and extractions throw it.</summary>
    public ArchiveException? Failure { get; set; }

    /// <summary>Starts an archive at <paramref name="osPath"/>; <paramref name="files"/> maps an inner path to its text (a path ending in / is a folder).</summary>
    public FakeArchiveReader Add(string osPath, IReadOnlyDictionary<string, string> files)
    {
        var list = new List<(ArchiveEntryInfo, byte[])>();
        var time = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        foreach (var (path, text) in files)
        {
            var isFolder = path.EndsWith('/');
            var bytes = Encoding.UTF8.GetBytes(text);
            list.Add((new ArchiveEntryInfo(ArchiveEntryInfo.NormalizePath(path), isFolder, isFolder ? 0 : bytes.Length, time, null), bytes));
        }

        _archives[osPath] = list;
        return this;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArchiveEntryInfo>> ReadEntriesAsync(string archivePath, string? password, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        if (ReadGate is { } gate)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return _archives.TryGetValue(archivePath, out var list) ? [.. list.Select(e => e.Info)] : throw new ArchiveException(ArchiveError.Unreadable, "Not an archive.");
    }

    /// <inheritdoc />
    public async Task ExtractEntryAsync(string archivePath, string entryPath, Stream destination, string? password, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _extractions);
        if (Failure is { } failure)
        {
            throw failure;
        }

        var wanted = ArchiveEntryInfo.NormalizePath(entryPath);
        var hit = _archives.TryGetValue(archivePath, out var list) ? list.FirstOrDefault(e => e.Info.Path.Equals(wanted, StringComparison.OrdinalIgnoreCase)) : default;
        if (hit.Info is null)
        {
            throw new ArchiveException(ArchiveError.EntryNotFound, "No such entry.");
        }

        await destination.WriteAsync(hit.Content, cancellationToken).ConfigureAwait(false);
    }
}
