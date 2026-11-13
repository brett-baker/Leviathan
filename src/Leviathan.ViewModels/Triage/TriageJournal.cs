using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leviathan.ViewModels.Triage;

/// <summary>What the journal knows about one file after replaying it: the latest decision, and whether its move has been dealt with.</summary>
/// <param name="Scope">The inbox id the decision belongs to, or empty for a plain queue.</param>
/// <param name="Path">The file as it was when the queue was made.</param>
/// <param name="Decision">The latest decision.</param>
/// <param name="MoveTo">The folder a kept file is waiting to be moved to; null when it is not waiting for a move.</param>
/// <param name="Moved">True once the move was done, handed to the queue, or dismissed.</param>
public sealed record JournalRecord(string Scope, string Path, TriageDecision Decision, string? MoveTo, bool Moved)
{
    /// <summary>True for a kept file whose move to <see cref="MoveTo"/> has not happened yet.</summary>
    public bool IsPendingMove => Decision == TriageDecision.Kept && !string.IsNullOrWhiteSpace(MoveTo) && !Moved;
}

/// <summary>
/// An append-only log of triage decisions, written the moment each is made (spec §10.1 "Decisions apply immediately", §3.4 "nothing is
/// ever lost"). Each line is small, so writing one per keypress costs nothing even for an inbox of 30,000 files whose full state lives
/// in <c>inboxes.json</c>. After a crash the next start replays it: decisions the session never got to save are recovered, and kept
/// files that were waiting to be moved are offered again. A torn last line (the process died mid-write) is skipped. Thread-safe;
/// all calls do file I/O, so callers run them off the UI thread.
/// </summary>
public sealed class TriageJournal(IFileSystem fileSystem, string path)
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly Lock _lock = new();

    private sealed record Line(string K, string P, string? S = null, string? D = null, string? M = null);

    /// <summary>Records a decision. <paramref name="moveTo"/> is the folder a kept file is to be moved to later, or null.</summary>
    public void AppendDecision(string scope, string filePath, TriageDecision decision, string? moveTo)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        Append(new Line("d", filePath, scope ?? string.Empty, decision.ToString(), string.IsNullOrWhiteSpace(moveTo) ? null : moveTo));
    }

    /// <summary>Records that the files' moves are dealt with (done, queued as a job, or dismissed), so they are not offered again.</summary>
    public void AppendMoved(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var lines = filePaths.Select(p => JsonSerializer.Serialize(new Line("m", p), Json)).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            Ensure();
            fileSystem.File.AppendAllLines(path, lines);
        }
    }

    /// <summary>The latest state of every file in the journal.</summary>
    public IReadOnlyList<JournalRecord> Replay()
    {
        lock (_lock)
        {
            return ReplayCore();
        }
    }

    /// <summary>Kept files still waiting to be moved.</summary>
    public IReadOnlyList<JournalRecord> PendingMoves() => [.. Replay().Where(r => r.IsPendingMove)];

    /// <summary>Rewrites the journal without the records <paramref name="remove"/> selects (written to a temporary file and renamed).</summary>
    public void Compact(Func<JournalRecord, bool> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        lock (_lock)
        {
            var keep = ReplayCore().Where(r => !remove(r)).ToList();
            var lines = new List<string>();
            foreach (var r in keep)
            {
                lines.Add(JsonSerializer.Serialize(new Line("d", r.Path, r.Scope, r.Decision.ToString(), r.MoveTo), Json));
                if (r.Moved)
                {
                    lines.Add(JsonSerializer.Serialize(new Line("m", r.Path), Json));
                }
            }

            if (lines.Count == 0)
            {
                if (fileSystem.File.Exists(path))
                {
                    fileSystem.File.Delete(path);
                }

                return;
            }

            Ensure();
            var temp = path + ".tmp";
            fileSystem.File.WriteAllLines(temp, lines);
            fileSystem.File.Move(temp, path, overwrite: true);
        }
    }

    private void Append(Line line)
    {
        var text = JsonSerializer.Serialize(line, Json);
        lock (_lock)
        {
            Ensure();
            fileSystem.File.AppendAllLines(path, [text]);
        }
    }

    private void Ensure()
    {
        if (fileSystem.Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            fileSystem.Directory.CreateDirectory(folder);
        }
    }

    private List<JournalRecord> ReplayCore()
    {
        var records = new Dictionary<string, JournalRecord>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        if (!fileSystem.File.Exists(path))
        {
            return [];
        }

        foreach (var text in fileSystem.File.ReadLines(path))
        {
            Line? line;
            try
            {
                line = JsonSerializer.Deserialize<Line>(text, Json);
            }
            catch (JsonException)
            {
                // A line cut short by a crash; everything before it is good.
                continue;
            }

            if (line is null || string.IsNullOrEmpty(line.P))
            {
                continue;
            }

            if (line.K == "d" && Enum.TryParse<TriageDecision>(line.D, out var decision))
            {
                if (!records.ContainsKey(line.P))
                {
                    order.Add(line.P);
                }

                records[line.P] = new JournalRecord(line.S ?? string.Empty, line.P, decision, line.M, false);
            }
            else if (line.K == "m" && records.TryGetValue(line.P, out var existing))
            {
                records[line.P] = existing with { Moved = true };
            }
        }

        return [.. order.Select(p => records[p])];
    }
}

/// <summary>
/// Writes a session's decisions to the <see cref="TriageJournal"/> as they happen (spec §10.1, §3.4). Writes are queued and done one
/// after another on the thread pool, so the UI thread never waits for the disk and a Keep followed by an Undo is journaled in that order.
/// A journal that cannot be written is not allowed to stop the triage: the failure is remembered in <see cref="LastError"/>.
/// </summary>
public sealed class TriageRecorder : IDisposable
{
    private readonly TriageSession _session;
    private readonly TriageJournal _journal;
    private readonly string _scope;
    private readonly Func<string?> _moveTo;
    private readonly Lock _lock = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Starts recording <paramref name="session"/>. <paramref name="scope"/> is the inbox id (empty for a plain queue); <paramref name="moveTo"/> gives the folder a kept file is to be moved to later, or null when it is not.</summary>
    public TriageRecorder(TriageSession session, TriageJournal journal, string scope, Func<string?> moveTo)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(moveTo);
        _session = session;
        _journal = journal;
        _scope = scope ?? string.Empty;
        _moveTo = moveTo;
        session.DecisionChanged += OnChanged;
    }

    /// <summary>Why the last write failed; null when they all worked.</summary>
    public string? LastError { get; private set; }

    /// <summary>Completes when every decision so far is on disk.</summary>
    public Task Flushed
    {
        get
        {
            lock (_lock)
            {
                return _tail;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _session.DecisionChanged -= OnChanged;

    private void OnChanged(object? sender, TriageItem item)
    {
        var path = item.Original.Location.Value;
        var decision = item.Decision;
        var moveTo = decision == TriageDecision.Kept ? _moveTo() : null;
        lock (_lock)
        {
            _tail = _tail.ContinueWith(
                _ =>
                {
                    try
                    {
                        _journal.AppendDecision(_scope, path, decision, moveTo);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LastError = ex.Message;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }
}
