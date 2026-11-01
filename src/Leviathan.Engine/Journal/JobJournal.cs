using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leviathan.Contracts;

namespace Leviathan.Engine.Journal;

/// <summary>What a journal record describes.</summary>
public enum JournalRecordType
{
    /// <summary>A job was queued.</summary>
    Enqueued,

    /// <summary>A job changed state.</summary>
    StateChanged,

    /// <summary>A job was removed from the list.</summary>
    Removed,
}

/// <summary>One line of the job journal.</summary>
public sealed record JournalRecord
{
    /// <summary>Record type.</summary>
    public required JournalRecordType Type { get; init; }

    /// <summary>The job.</summary>
    public required Guid JobId { get; init; }

    /// <summary>When the record was written.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The request (Enqueued records only).</summary>
    public JobRequest? Request { get; init; }

    /// <summary>Lane (Enqueued records only).</summary>
    public JobLane? Lane { get; init; }

    /// <summary>Device key (Enqueued records only).</summary>
    public string? DeviceKey { get; init; }

    /// <summary>Title (Enqueued records only).</summary>
    public string? Title { get; init; }

    /// <summary>New state (StateChanged records only).</summary>
    public JobState? State { get; init; }
}

/// <summary>A job reconstructed from the journal, ready to be re-queued after a restart.</summary>
/// <param name="JobId">Original job ID.</param>
/// <param name="Request">The request.</param>
/// <param name="Lane">Lane.</param>
/// <param name="DeviceKey">Device key.</param>
/// <param name="Title">Title.</param>
/// <param name="LastState">Last recorded state.</param>
public sealed record RecoveredJob(Guid JobId, JobRequest Request, JobLane Lane, string? DeviceKey, string Title, JobState LastState);

/// <summary>Durable record of the queue, so jobs survive UI crashes, worker restarts and reboots (spec §12.3).</summary>
public interface IJobJournal
{
    /// <summary>Appends a record. Must be durable when it returns.</summary>
    void Append(JournalRecord record);

    /// <summary>Reads every readable record, skipping a torn final line.</summary>
    IReadOnlyList<JournalRecord> ReadAll();

    /// <summary>Rewrites the journal to contain only <paramref name="records"/>.</summary>
    void Compact(IEnumerable<JournalRecord> records);
}

/// <summary>Replays journal records into the set of unfinished jobs.</summary>
public static class JournalReplay
{
    /// <summary>
    /// Returns the jobs that had not finished. A job that was Running or Waiting when the process died comes back as
    /// Queued (its executor resumes from its own per-file journal); a Paused job stays Paused.
    /// </summary>
    public static IReadOnlyList<RecoveredJob> Recover(IEnumerable<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var jobs = new Dictionary<Guid, RecoveredJob>();
        var order = new List<Guid>();
        foreach (var record in records)
        {
            switch (record.Type)
            {
                case JournalRecordType.Enqueued when record.Request is not null:
                    jobs[record.JobId] = new RecoveredJob(record.JobId, record.Request, record.Lane ?? JobLane.Operations, record.DeviceKey, record.Title ?? record.Request.Kind.ToString(), record.Request.StartPaused ? JobState.Paused : JobState.Queued);
                    order.Add(record.JobId);
                    break;
                case JournalRecordType.StateChanged when record.State is { } state && jobs.TryGetValue(record.JobId, out var job):
                    jobs[record.JobId] = job with { LastState = state };
                    break;
                case JournalRecordType.Removed:
                    jobs.Remove(record.JobId);
                    break;
            }
        }

        return [.. order
            .Distinct()
            .Where(jobs.ContainsKey)
            .Select(id => jobs[id])
            .Where(j => j.LastState is not (JobState.Completed or JobState.Failed or JobState.Cancelled))
            .Select(j => j.LastState is JobState.Running or JobState.Waiting ? j with { LastState = JobState.Queued } : j)];
    }
}

/// <summary>
/// Journal stored as JSON lines. Each append opens, writes one line and flushes, so a crash loses at most the line being
/// written; a torn last line is skipped on read. Compaction writes a new file and swaps it in.
/// </summary>
public sealed class FileJobJournal : IJobJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly Lock _lock = new();

    /// <summary>Creates a journal at <paramref name="path"/>.</summary>
    public FileJobJournal(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fileSystem = fileSystem;
        _path = path;
        var directory = _fileSystem.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }
    }

    /// <inheritdoc />
    public void Append(JournalRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
        lock (_lock)
        {
            using var stream = _fileSystem.File.Open(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(line);
            writer.Flush();
            stream.Flush();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalRecord> ReadAll()
    {
        lock (_lock)
        {
            if (!_fileSystem.File.Exists(_path))
            {
                return [];
            }

            var records = new List<JournalRecord>();
            foreach (var line in _fileSystem.File.ReadAllLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<JournalRecord>(line, JsonOptions) is { } record)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // A line torn by a crash mid-write; everything before it is intact.
                }
            }

            return records;
        }
    }

    /// <inheritdoc />
    public void Compact(IEnumerable<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var temp = _path + ".tmp";
        var text = string.Concat(records.Select(r => JsonSerializer.Serialize(r, JsonOptions) + "\n"));
        lock (_lock)
        {
            _fileSystem.File.WriteAllText(temp, text);
            _fileSystem.File.Move(temp, _path, overwrite: true);
        }
    }
}

/// <summary>In-memory journal for tests and for processes that opt out of persistence.</summary>
public sealed class InMemoryJobJournal : IJobJournal
{
    private readonly Lock _lock = new();
    private readonly List<JournalRecord> _records = [];

    /// <inheritdoc />
    public void Append(JournalRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalRecord> ReadAll()
    {
        lock (_lock)
        {
            return [.. _records];
        }
    }

    /// <inheritdoc />
    public void Compact(IEnumerable<JournalRecord> records)
    {
        lock (_lock)
        {
            _records.Clear();
            _records.AddRange(records);
        }
    }
}
