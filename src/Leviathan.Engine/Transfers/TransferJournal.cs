using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Leviathan.Contracts;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// What a copy or move had finished when it last wrote its journal (spec §12.1 step 4): steps that are done, how many bytes of a file in
/// progress are safely on disk, and the outcome of each finished item. Rebuilt by replaying the journal, so a restart, a reboot or a
/// network drop continues where the job stopped.
/// </summary>
public sealed class TransferProgress
{
    /// <summary>Nothing done yet.</summary>
    public static TransferProgress Empty => new();

    /// <summary>The (item, step) pairs that completed.</summary>
    public HashSet<(int Item, int Step)> DoneSteps { get; } = [];

    /// <summary>Bytes of a file's part file that were flushed to disk, per (item, step).</summary>
    public Dictionary<(int Item, int Step), long> Confirmed { get; } = [];

    /// <summary>Finished items and how they ended.</summary>
    public Dictionary<int, (ItemStatus Status, string? Error)> Items { get; } = [];
}

/// <summary>Where the progress of unfinished transfers is kept, one journal per job.</summary>
public interface ITransferJournal
{
    /// <summary>Replays the journal of <paramref name="jobId"/>; empty when there is none. A torn last line is ignored.</summary>
    TransferProgress Load(Guid jobId);

    /// <summary>A step completed (its file is in place, a move's source is gone).</summary>
    void StepDone(Guid jobId, int item, int step);

    /// <summary>The first <paramref name="bytes"/> of a file's part file are flushed to disk; a resume may continue from there.</summary>
    void FileConfirmed(Guid jobId, int item, int step, long bytes);

    /// <summary>An item ended with <paramref name="status"/>.</summary>
    void ItemFinished(Guid jobId, int item, ItemStatus status, string? error);

    /// <summary>Forgets the journal of a finished job.</summary>
    void Delete(Guid jobId);
}

/// <summary>Keeps progress in memory, for tests and hosts without a data folder.</summary>
public sealed class InMemoryTransferJournal : ITransferJournal
{
    private readonly Dictionary<Guid, TransferProgress> _progress = [];

    /// <inheritdoc />
    public TransferProgress Load(Guid jobId)
    {
        lock (_progress)
        {
            var copy = new TransferProgress();
            if (_progress.TryGetValue(jobId, out var p))
            {
                copy.DoneSteps.UnionWith(p.DoneSteps);
                foreach (var c in p.Confirmed)
                {
                    copy.Confirmed[c.Key] = c.Value;
                }

                foreach (var i in p.Items)
                {
                    copy.Items[i.Key] = i.Value;
                }
            }

            return copy;
        }
    }

    /// <inheritdoc />
    public void StepDone(Guid jobId, int item, int step)
    {
        lock (_progress)
        {
            Get(jobId).DoneSteps.Add((item, step));
        }
    }

    /// <inheritdoc />
    public void FileConfirmed(Guid jobId, int item, int step, long bytes)
    {
        lock (_progress)
        {
            Get(jobId).Confirmed[(item, step)] = bytes;
        }
    }

    /// <inheritdoc />
    public void ItemFinished(Guid jobId, int item, ItemStatus status, string? error)
    {
        lock (_progress)
        {
            Get(jobId).Items[item] = (status, error);
        }
    }

    /// <inheritdoc />
    public void Delete(Guid jobId)
    {
        lock (_progress)
        {
            _progress.Remove(jobId);
        }
    }

    private TransferProgress Get(Guid jobId)
    {
        if (!_progress.TryGetValue(jobId, out var p))
        {
            _progress[jobId] = p = new TransferProgress();
        }

        return p;
    }
}

/// <summary>
/// One append-only JSON-lines file per job. A crash can leave the last line half written; replay skips any line it cannot read, so the
/// worst case is that the last step or confirmation is lost and redone (every step is safe to run twice). Confirmations are flushed
/// to disk, step records only to the operating system (they survive a killed process; after a power cut the step simply repeats).
/// </summary>
public sealed class FileTransferJournal : ITransferJournal, IDisposable
{
    private readonly IFileSystem _fs;
    private readonly string _folder;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, FileSystemStream> _open = [];

    /// <summary>Creates the journal in <paramref name="folder"/>.</summary>
    public FileTransferJournal(IFileSystem fileSystem, string folder)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _fs = fileSystem;
        _folder = folder;
    }

    /// <inheritdoc />
    public TransferProgress Load(Guid jobId)
    {
        var progress = new TransferProgress();
        lock (_lock)
        {
            CloseLocked(jobId);
            var path = PathFor(jobId);
            if (!_fs.File.Exists(path))
            {
                return progress;
            }

            string[] lines;
            try
            {
                lines = _fs.File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return progress;
            }

            foreach (var line in lines)
            {
                Apply(progress, line);
            }
        }

        return progress;
    }

    /// <inheritdoc />
    public void StepDone(Guid jobId, int item, int step) => Append(jobId, new Line("s", item, step, 0, null, null), durable: false);

    /// <inheritdoc />
    public void FileConfirmed(Guid jobId, int item, int step, long bytes) => Append(jobId, new Line("c", item, step, bytes, null, null), durable: true);

    /// <inheritdoc />
    public void ItemFinished(Guid jobId, int item, ItemStatus status, string? error) =>
        Append(jobId, new Line("i", item, 0, 0, status.ToString(), error), durable: false);

    /// <inheritdoc />
    public void Delete(Guid jobId)
    {
        lock (_lock)
        {
            CloseLocked(jobId);
            try
            {
                var path = PathFor(jobId);
                if (_fs.File.Exists(path))
                {
                    _fs.File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover journal of a finished job is harmless; it is ignored once the job id is gone.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var stream in _open.Values)
            {
                stream.Dispose();
            }

            _open.Clear();
        }
    }

    private static void Apply(TransferProgress progress, string line)
    {
        try
        {
            var record = JsonSerializer.Deserialize<Line>(line);
            if (record is null)
            {
                return;
            }

            switch (record.T)
            {
                case "s":
                    progress.DoneSteps.Add((record.I, record.S));
                    break;
                case "c":
                    progress.Confirmed[(record.I, record.S)] = record.B;
                    break;
                case "i" when Enum.TryParse<ItemStatus>(record.R, out var status):
                    progress.Items[record.I] = (status, record.E);
                    break;
            }
        }
        catch (JsonException)
        {
            // A torn or damaged line: skip it, the step it described will be redone.
        }
    }

    private void Append(Guid jobId, Line line, bool durable)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line) + "\n");
        lock (_lock)
        {
            if (!_open.TryGetValue(jobId, out var stream))
            {
                _fs.Directory.CreateDirectory(_folder);
                stream = _fs.FileStream.New(PathFor(jobId), FileMode.Append, FileAccess.Write, FileShare.Read);
                _open[jobId] = stream;
            }

            stream.Write(bytes);
            stream.Flush();
            if (durable)
            {
                stream.Flush(flushToDisk: true);
            }
        }
    }

    private void CloseLocked(Guid jobId)
    {
        if (_open.Remove(jobId, out var stream))
        {
            stream.Dispose();
        }
    }

    private string PathFor(Guid jobId) => _fs.Path.Combine(_folder, jobId.ToString("N") + ".progress");

    // Short property names keep a hundred-thousand-file journal small.
    private sealed record Line(string T, int I, int S, long B, string? R, string? E);
}
