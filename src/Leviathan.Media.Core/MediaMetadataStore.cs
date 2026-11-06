using System.Collections.Concurrent;

namespace Leviathan.Media;

/// <summary>
/// Which version of a file a cached answer is for (spec §9.3): the full path, the size and the last-modified time. A change in any of
/// the three makes an old answer useless, which is how a file that was unreadable gets another chance when it is replaced.
/// </summary>
/// <param name="Path">The full path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ModifiedUtc">Last write time, UTC.</param>
public readonly record struct MediaFileKey(string Path, long Size, DateTime ModifiedUtc)
{
    /// <summary>An identity for "this version of this file", for keeping track of work in progress.</summary>
    public string Id => $"{Path.ToUpperInvariant()}|{Size}|{ModifiedUtc.Ticks}";

    /// <summary>True when <paramref name="other"/> is the same file in the same state (the path compares without regard to case, as Windows does).</summary>
    public bool Matches(MediaFileKey other) =>
        Size == other.Size
        && ModifiedUtc.Ticks == other.ModifiedUtc.Ticks
        && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What is known about one file.</summary>
public enum MetadataState
{
    /// <summary>ffprobe answered; <see cref="MediaMetadataEntry.Info"/> is set.</summary>
    Ok = 0,

    /// <summary>The file timed out, failed or crashed the probe; it is not tried again until it changes (spec §9.3).</summary>
    Unreadable = 1,

    /// <summary>A probe was started and has not finished. Only ever seen after a crash, where it becomes <see cref="Unreadable"/>.</summary>
    Probing = 2,

    /// <summary>ffprobe is not installed. Never stored: installing it makes every file probe-able again.</summary>
    ToolMissing = 3,
}

/// <summary>A cached answer about one file.</summary>
/// <param name="Key">The file version it describes.</param>
/// <param name="State">What happened.</param>
/// <param name="Info">The metadata (<see cref="MetadataState.Ok"/> only).</param>
/// <param name="Message">Why the file is unreadable, for the tooltip and "retry this file".</param>
/// <param name="At">When the answer was made.</param>
public sealed record MediaMetadataEntry(MediaFileKey Key, MetadataState State, MediaInfo? Info, string? Message, DateTimeOffset At);

/// <summary>Where answers are kept between runs (SQLite in the app, memory in tests).</summary>
public interface IMediaMetadataStore
{
    /// <summary>The answer for exactly this version of the file, or null when there is none (including when the file has changed since).</summary>
    MediaMetadataEntry? Get(MediaFileKey key);

    /// <summary>Stores an answer, replacing any earlier one for the path.</summary>
    void Put(MediaMetadataEntry entry);

    /// <summary>Records that a probe of this file is starting, so a crash during it is noticed afterwards.</summary>
    void MarkProbing(MediaFileKey key, DateTimeOffset at);

    /// <summary>
    /// Turns every probe that never finished into <see cref="MetadataState.Unreadable"/> with <paramref name="reason"/>, and returns how
    /// many there were. Called once when the Media process starts: whatever it was working on when it died is the suspect, and is not
    /// tried again, so one bad file cannot make the process crash in a loop.
    /// </summary>
    int QuarantineInterrupted(DateTimeOffset at, string reason);

    /// <summary>Forgets the answer for a path ("retry this file"). Returns true when there was one.</summary>
    bool Forget(string path);

    /// <summary>Every file currently marked unreadable, newest first (the Background tab lists them).</summary>
    IReadOnlyList<MediaMetadataEntry> Unreadable();
}

/// <summary>A store in memory: for tests, and for a session where the database is not available.</summary>
public sealed class InMemoryMediaMetadataStore : IMediaMetadataStore
{
    private readonly ConcurrentDictionary<string, MediaMetadataEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public MediaMetadataEntry? Get(MediaFileKey key) => _entries.TryGetValue(key.Path, out var entry) && entry.Key.Matches(key) ? entry : null;

    /// <inheritdoc />
    public void Put(MediaMetadataEntry entry) => _entries[entry.Key.Path] = entry;

    /// <inheritdoc />
    public void MarkProbing(MediaFileKey key, DateTimeOffset at) =>
        _entries[key.Path] = new MediaMetadataEntry(key, MetadataState.Probing, null, null, at);

    /// <inheritdoc />
    public int QuarantineInterrupted(DateTimeOffset at, string reason)
    {
        var count = 0;
        foreach (var (path, entry) in _entries)
        {
            if (entry.State == MetadataState.Probing)
            {
                _entries[path] = entry with { State = MetadataState.Unreadable, Message = reason, At = at };
                count++;
            }
        }

        return count;
    }

    /// <inheritdoc />
    public bool Forget(string path) => _entries.TryRemove(path, out _);

    /// <inheritdoc />
    public IReadOnlyList<MediaMetadataEntry> Unreadable() =>
        [.. _entries.Values.Where(e => e.State == MetadataState.Unreadable).OrderByDescending(e => e.At)];
}
