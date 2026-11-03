namespace Leviathan.ViewModels.Undo;

/// <summary>What an undoable action did (spec §12.5).</summary>
public enum UndoKind
{
    /// <summary>One item got a new name; undo gives it the old one.</summary>
    Rename,

    /// <summary>Items went to the Recycle Bin or a share's recycle folder; undo puts them back.</summary>
    Delete,
}

/// <summary>One item that was moved from <paramref name="From"/> to <paramref name="To"/> by an action: the new name, or the place in the bin.</summary>
public sealed record ItemMove(string From, string To);

/// <summary>An action that can be undone and, once undone, redone. Stored on disk so it survives a restart of the app.</summary>
/// <param name="Id">Identifies the entry.</param>
/// <param name="Kind">What the action was.</param>
/// <param name="Title">Shown in the tooltips of Undo and Redo, such as "Delete 3 items".</param>
/// <param name="At">When it happened; entries expire after <see cref="UndoService.Lifetime"/>.</param>
/// <param name="Moves">What was moved where, as the action did it.</param>
public sealed record UndoEntry(Guid Id, UndoKind Kind, string Title, DateTimeOffset At, IReadOnlyList<ItemMove> Moves);

/// <summary>The two stacks as stored: newest entry last.</summary>
public sealed record UndoState(IReadOnlyList<UndoEntry> Undo, IReadOnlyList<UndoEntry> Redo)
{
    /// <summary>Nothing to undo or redo.</summary>
    public static UndoState Empty { get; } = new([], []);
}

/// <summary>Keeps the undo stacks between runs (spec §12.5: undo survives a restart). The real one is JSON in the user's app data.</summary>
public interface IUndoStorage
{
    /// <summary>Reads the stacks; damaged or missing storage gives <see cref="UndoState.Empty"/>.</summary>
    UndoState Load();

    /// <summary>Writes the stacks. Must be atomic and must not throw for a disk problem, which only costs the undo history.</summary>
    void Save(UndoState state);
}

/// <summary>What a step did, as reported by <see cref="IUndoActions"/>.</summary>
/// <param name="Done">Items that ended up where they were asked to.</param>
/// <param name="Problems">One sentence for each item that could not be done, such as "'a.txt' already exists in this folder."</param>
public sealed record StepResult(IReadOnlyList<ItemMove> Done, IReadOnlyList<string> Problems);

/// <summary>
/// The file work behind undo and redo. The seam to the worker, whose journaled restore and delete jobs do it (spec §12.1), and to
/// fakes in the tests.
/// </summary>
public interface IUndoActions
{
    /// <summary>Moves each item from <see cref="ItemMove.From"/> to <see cref="ItemMove.To"/> without overwriting anything.</summary>
    Task<StepResult> MoveAsync(IReadOnlyList<ItemMove> moves, CancellationToken cancellationToken);

    /// <summary>Sends the items to the Recycle Bin or a share's recycle folder again; the result says where each went.</summary>
    Task<StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

/// <summary>The outcome of Undo or Redo.</summary>
/// <param name="Done">True when at least one item was changed.</param>
/// <param name="Message">Text for the status bar.</param>
/// <param name="Touched">Paths now holding items the step changed, so the view can reload and select them.</param>
public sealed record UndoOutcome(bool Done, string Message, IReadOnlyList<string> Touched);

/// <summary>
/// The undo and redo stacks of the app (spec §12.5): rename and delete today, moves and the rest as their slices land. One service
/// for all windows, as in Explorer, so Ctrl+Z undoes the last thing done anywhere. Entries are persisted on every change, expire
/// after a week and the list is capped, so the file stays small.
/// </summary>
public sealed class UndoService : IDisposable
{
    /// <summary>How long an action can be undone.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>How many actions are remembered.</summary>
    public const int Capacity = 100;

    private readonly IUndoStorage _storage;
    private readonly IUndoActions _actions;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly List<UndoEntry> _undo;
    private readonly List<UndoEntry> _redo;
    private Task _saving = Task.CompletedTask;

    /// <summary>Creates the service and loads what was stored, dropping what has expired.</summary>
    public UndoService(IUndoStorage storage, IUndoActions actions, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(actions);
        _storage = storage;
        _actions = actions;
        _time = time ?? TimeProvider.System;
        var state = storage.Load();
        var oldest = _time.GetUtcNow() - Lifetime;
        _undo = [.. state.Undo.Where(e => e.At >= oldest)];
        _redo = [.. state.Redo.Where(e => e.At >= oldest)];
    }

    /// <summary>Raised, on whatever thread changed the stacks, after a record, undo or redo.</summary>
    public event EventHandler? Changed;

    /// <summary>Completes when every change so far has been written; the tests wait on it, the app never needs to.</summary>
    public Task Saved
    {
        get
        {
            lock (_lock)
            {
                return _saving;
            }
        }
    }

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo
    {
        get
        {
            lock (_lock)
            {
                return _undo.Count > 0;
            }
        }
    }

    /// <summary>True when something was undone and can be redone.</summary>
    public bool CanRedo
    {
        get
        {
            lock (_lock)
            {
                return _redo.Count > 0;
            }
        }
    }

    /// <summary>What Undo would undo, such as "Rename a.txt to b.txt"; null when there is nothing.</summary>
    public string? UndoTitle
    {
        get
        {
            lock (_lock)
            {
                return _undo.Count > 0 ? _undo[^1].Title : null;
            }
        }
    }

    /// <summary>What Redo would redo; null when there is nothing.</summary>
    public string? RedoTitle
    {
        get
        {
            lock (_lock)
            {
                return _redo.Count > 0 ? _redo[^1].Title : null;
            }
        }
    }

    /// <summary>Remembers that <paramref name="from"/> was renamed to <paramref name="to"/>. A new action ends the redo history, as everywhere.</summary>
    public void RecordRename(string from, string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        Record(UndoKind.Rename, $"Rename {NameOf(from)} to {NameOf(to)}", [new ItemMove(from, to)]);
    }

    /// <summary>Remembers that items were sent to the Recycle Bin or a share's recycle folder, and where each went.</summary>
    public void RecordDelete(IReadOnlyList<ItemMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.Count > 0)
        {
            Record(UndoKind.Delete, moves.Count == 1 ? $"Delete {NameOf(moves[0].From)}" : $"Delete {moves.Count} items", moves);
        }
    }

    /// <summary>Undoes the last action.</summary>
    public async Task<UndoOutcome> UndoAsync(CancellationToken cancellationToken)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UndoEntry entry;
            lock (_lock)
            {
                if (_undo.Count == 0)
                {
                    return new UndoOutcome(false, "There is nothing to undo.", []);
                }

                entry = _undo[^1];
            }

            var result = await _actions.MoveAsync([.. entry.Moves.Select(m => new ItemMove(m.To, m.From))], cancellationToken).ConfigureAwait(false);
            return Finish(entry, result, undoing: true, verb: "Undone");
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Redoes the last undone action.</summary>
    public async Task<UndoOutcome> RedoAsync(CancellationToken cancellationToken)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UndoEntry entry;
            lock (_lock)
            {
                if (_redo.Count == 0)
                {
                    return new UndoOutcome(false, "There is nothing to redo.", []);
                }

                entry = _redo[^1];
            }

            var result = entry.Kind == UndoKind.Delete
                ? await _actions.DeleteAsync([.. entry.Moves.Select(m => m.From)], cancellationToken).ConfigureAwait(false)
                : await _actions.MoveAsync(entry.Moves, cancellationToken).ConfigureAwait(false);
            return Finish(entry, result, undoing: false, verb: "Redone");
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _busy.Dispose();

    private static string NameOf(string path) => path.TrimEnd('\\', '/').Split('\\', '/')[^1];

    private void Record(UndoKind kind, string title, IReadOnlyList<ItemMove> moves)
    {
        lock (_lock)
        {
            _undo.Add(new UndoEntry(Guid.NewGuid(), kind, title, _time.GetUtcNow(), moves));
            if (_undo.Count > Capacity)
            {
                _undo.RemoveRange(0, _undo.Count - Capacity);
            }

            _redo.Clear();
            Persist();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the entry to the other stack if the step worked. When only some items could be put back, the entry keeps the ones that
    /// were and the rest are dropped with an explanation, so a stuck item cannot block the history; when none could, the entry
    /// stays so the user can fix the cause (a name taken, a file in use) and try again.
    /// </summary>
    private UndoOutcome Finish(UndoEntry entry, StepResult result, bool undoing, string verb)
    {
        // The folder the user is looking at changed where the item now is, except when a delete is redone and it left.
        var touched = result.Done.Select(d => !undoing && entry.Kind == UndoKind.Delete ? d.From : d.To).ToList();
        lock (_lock)
        {
            var from = undoing ? _undo : _redo;
            var to = undoing ? _redo : _undo;
            if (result.Done.Count > 0)
            {
                from.Remove(entry);

                // Entries are stored as the action did them (From = before, To = after). Undo reports the reverse move, so turn it
                // back; redo already reports the action again, which for a delete also gives the item's new place in the bin.
                IReadOnlyList<ItemMove> moves = undoing ? [.. result.Done.Select(d => new ItemMove(d.To, d.From))] : result.Done;
                to.Add(entry with { Moves = moves, At = _time.GetUtcNow() });
                Persist();
            }
        }

        if (result.Done.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        var summary = result.Done.Count > 0 ? $"{verb}: {entry.Title}" : $"Could not {(undoing ? "undo" : "redo")} {entry.Title}";
        var message = result.Problems.Count == 0 ? summary : $"{summary}. {string.Join(" ", result.Problems.Take(2))}";
        return new UndoOutcome(result.Done.Count > 0, message, touched);
    }

    /// <summary>Queues a write of the stacks (called with the lock held). Writes run one after another on the thread pool, never on the caller (spec §3.2).</summary>
    private void Persist()
    {
        var state = new UndoState([.. _undo], [.. _redo]);
        _saving = _saving.ContinueWith(
            _ =>
            {
                try
                {
                    _storage.Save(state);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Losing the stored history is better than failing the action that was just done.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }
}
