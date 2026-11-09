using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Playback;

namespace Leviathan.ViewModels.Triage;

/// <summary>
/// One run through a queue of videos (spec §10.1): the file in the player, the decisions made so far, and the commands the keys of §10.2
/// call. The aim is under ten seconds a file without the mouse, so every command is one call: Keep and Discard apply at once and open the
/// next file after the configured delay (a decision key pressed during the delay opens the next file first, so mashing keys never decides
/// the same file twice), Skip leaves the file alone, Previous goes back, Undo takes the last decision back and returns to that file. A
/// file is closed in the player before it is discarded or moved, because Windows will not move an open file. Everything outside the
/// session, the player, the file actions and the clock, is behind an interface, so the whole run is unit-tested with fakes. Used from
/// the UI thread; commands that overlap are queued, not interleaved.
/// </summary>
public sealed class TriageSession : ObservableObject, IAsyncDisposable
{
    private readonly List<TriageItem> _items;
    private readonly IVideoEngine _engine;
    private readonly ITriageActions _actions;
    private readonly TriageOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stack<HistoryEntry> _history = new();
    private CancellationTokenSource _navigation = new();
    private int _index = -1;
    private bool _finished;
    private bool _advancePending;
    private string? _error;

    /// <summary>Starts a session over <paramref name="queue"/>; call <see cref="StartAsync"/> to open the first file.</summary>
    public TriageSession(IReadOnlyList<FileEntry> queue, IVideoEngine engine, ITriageActions actions, TriageOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _items = [.. queue.Select(f => new TriageItem(f))];
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _options = options ?? new TriageOptions();
        _time = time ?? TimeProvider.System;
        Keys = new PlaybackController(engine);
    }

    private sealed record HistoryEntry(TriageItem Item, TriageDecision Previous, Func<Task>? PreviousUndo, FileEntry PreviousFile);

    /// <summary>The playback keys over the session's player (Space, jumps, skim…), so the window needs only one object.</summary>
    public PlaybackController Keys { get; }

    /// <summary>The queue with each file's decision.</summary>
    public IReadOnlyList<TriageItem> Items => _items;

    /// <summary>The position of the current file in the queue, or -1 before <see cref="StartAsync"/>.</summary>
    public int Index => _index;

    /// <summary>The current file; null before the start, when the queue is empty and after the last file.</summary>
    public TriageItem? Current => _index >= 0 && _index < _items.Count && !_finished ? _items[_index] : null;

    /// <summary>True once the last file has been dealt with (or the queue was empty): the window shows the summary.</summary>
    public bool IsFinished => _finished;

    /// <summary>Why the last command did not do what was asked (a file that would not open, a delete that failed); null when it went well.</summary>
    public string? LastError
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Raised once, when the queue ends.</summary>
    public event EventHandler? Finished;

    /// <summary>Files kept.</summary>
    public int Kept => Count(TriageDecision.Kept);

    /// <summary>Files discarded.</summary>
    public int Discarded => Count(TriageDecision.Discarded);

    /// <summary>Files skipped.</summary>
    public int Skipped => Count(TriageDecision.Skipped);

    /// <summary>Bytes in the discarded files: the space reclaimed so far.</summary>
    public long ReclaimedBytes => _items.Where(i => i.Decision == TriageDecision.Discarded).Sum(i => i.File.Size);

    /// <summary>"48 / 312 · 12 kept · 30 discarded · 6 skipped" (spec §10.1).</summary>
    public string ProgressText =>
        $"{(_finished ? _items.Count : Math.Max(0, _index + 1)):N0} / {_items.Count:N0} · {Kept:N0} kept · {Discarded:N0} discarded · {Skipped:N0} skipped";

    /// <summary>"1.4 GB reclaimed" beside the progress text.</summary>
    public string ReclaimedText => $"{DisplayFormat.Size(ReclaimedBytes)} reclaimed";

    /// <summary>Opens the file at <paramref name="startIndex"/> (the first by default). Does nothing when already started; an empty queue is finished at once.</summary>
    public async Task StartAsync(int startIndex = 0, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index >= 0 || _finished)
            {
                return;
            }

            if (_items.Count == 0)
            {
                Finish();
                return;
            }

            await GoToCoreAsync(Math.Clamp(startIndex, 0, _items.Count - 1)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Goes to the file at <paramref name="index"/> (a click in the queue list); the file already showing is left playing.</summary>
    public async Task GoToAsync(int index)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (index < 0 || index >= _items.Count || (index == _index && !_finished))
            {
                return;
            }

            CancelNavigation();
            _finished = false;
            await GoToCoreAsync(index).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// "Review discarded" at the end of the queue (spec §10.1): takes the decision about <paramref name="item"/> back, whatever its age, so a
    /// discarded file is restored (and a kept one untagged and moved home). The file shows as undecided again; Ctrl+Z history of it is dropped.
    /// </summary>
    public async Task RestoreAsync(TriageItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_items.Contains(item) || item.Decision is not (TriageDecision.Kept or TriageDecision.Discarded))
            {
                return;
            }

            try
            {
                if (item.Undo is { } undo)
                {
                    await undo().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LastError = $"{item.File.Name} could not be put back: {ex.Message}";
                return;
            }

            item.Decision = TriageDecision.None;
            item.Undo = null;
            item.File = item.Original;
            var kept = _history.Reverse().Where(h => h.Item != item).ToList();
            _history.Clear();
            foreach (var entry in kept)
            {
                _history.Push(entry);
            }

            LastError = null;
            RaiseCounts();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Enter or G: keep the file, then the next one.</summary>
    public Task KeepAsync() => DecideAsync(TriageDecision.Kept);

    /// <summary>Delete or X: discard the file (a soft delete), then the next one.</summary>
    public Task DiscardAsync() => DecideAsync(TriageDecision.Discarded);

    /// <summary>Down or Tab: no decision, the next file.</summary>
    public async Task SkipAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Skipping is moving on by hand, so it moves at once whatever the delay after a decision; a decision already made is left alone.
            if (_advancePending)
            {
                CancelNavigation();
                _advancePending = false;
                await MoveNextCoreAsync().ConfigureAwait(false);
            }

            if (Current is not { } item)
            {
                return;
            }

            if (item.Decision == TriageDecision.None)
            {
                _history.Push(new HistoryEntry(item, TriageDecision.None, null, item.File));
                item.Decision = TriageDecision.Skipped;
                RaiseCounts();
            }

            LastError = null;
            await MoveNextCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Up or Shift+Tab: the file before this one (the last file when the queue has ended).</summary>
    public async Task PreviousAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelNavigation();
            var target = _finished ? _items.Count - 1 : _index - 1;
            if (target >= 0)
            {
                _finished = false;
                await GoToCoreAsync(target).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ctrl+Z: takes the last decision back and returns to that file.</summary>
    public async Task UndoAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelNavigation();
            if (_history.Count == 0)
            {
                return;
            }

            var entry = _history.Pop();
            try
            {
                if (entry.Item.Undo is { } undo)
                {
                    await undo().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _history.Push(entry);
                LastError = $"{entry.Item.File.Name} could not be put back: {ex.Message}";
                return;
            }

            entry.Item.Decision = entry.Previous;
            entry.Item.Undo = entry.PreviousUndo;
            entry.Item.File = entry.PreviousFile;
            _finished = false;
            LastError = null;
            await GoToCoreAsync(_items.IndexOf(entry.Item)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Keys 1 to 9: toggles a colour tag on the current file; the file stays up and nothing advances.</summary>
    public async Task ToggleTagAsync(int tagNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tagNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tagNumber, 9);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is not { } item)
            {
                return;
            }

            try
            {
                var on = await _actions.ToggleTagAsync(item.File, tagNumber, CancellationToken.None).ConfigureAwait(false);
                if (on)
                {
                    item.Tags.Add(tagNumber);
                }
                else
                {
                    item.Tags.Remove(tagNumber);
                }

                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LastError = $"Tag {tagNumber} could not be changed on {item.File.Name}: {ex.Message}";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ctrl+0: removes every colour tag from the current file.</summary>
    public async Task ClearTagsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is not { } item)
            {
                return;
            }

            try
            {
                await _actions.ClearTagsAsync(item.File, CancellationToken.None).ConfigureAwait(false);
                item.Tags.Clear();
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LastError = $"The tags could not be cleared on {item.File.Name}: {ex.Message}";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The files after the current one, for the player to read ahead (spec §10.1: the next two files are read ahead on a share).</summary>
    public IReadOnlyList<FileEntry> Upcoming(int count) =>
        [.. _items.Skip(_index + 1).Where(i => i.IsPlayable).Take(Math.Max(0, count)).Select(i => i.File)];

    /// <summary>How the session stands, for the summary at the end of the queue.</summary>
    public TriageSummary Summary() => new(
        Kept, Discarded, Skipped, _items.Count(i => i.Decision == TriageDecision.None), ReclaimedBytes,
        [.. _items.Where(i => i.Decision == TriageDecision.Discarded).Select(i => i.File)]);

    /// <summary>Stops everything pending and closes the player's file. The player itself belongs to the caller.</summary>
    public async ValueTask DisposeAsync()
    {
        CancelNavigation();
        _navigation.Dispose();
        try
        {
            await _engine.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The player was already gone.
        }

        _gate.Dispose();
    }

    private async Task DecideAsync(TriageDecision decision)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var advanceAfter = false;
        try
        {
            // A decision pressed while the last one waits to advance moves on first, so it lands on the next file, not the same one again.
            if (_advancePending)
            {
                CancelNavigation();
                _advancePending = false;
                await MoveNextCoreAsync().ConfigureAwait(false);
            }

            if (Current is not { } item)
            {
                return;
            }

            var previous = new HistoryEntry(item, item.Decision, item.Undo, item.File);
            {
                // Windows will not move or delete a file the player has open.
                await _engine.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                if (item.Decision is TriageDecision.Kept or TriageDecision.Discarded && item.Undo is { } replaced)
                {
                    // Deciding again about a file already kept or discarded: the old decision is taken back first.
                    try
                    {
                        await replaced().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        LastError = $"{item.File.Name} could not be put back before deciding again: {ex.Message}";
                        return;
                    }

                    item.Undo = null;
                    item.Decision = TriageDecision.None;
                }

                TriageActionResult result;
                try
                {
                    result = decision == TriageDecision.Kept
                        ? await _actions.KeepAsync(item.File, CancellationToken.None).ConfigureAwait(false)
                        : await _actions.DiscardAsync(item.File, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // The file stays where it is and stays on screen, so the user sees why and can try again or skip.
                    LastError = $"{item.File.Name} could not be {(decision == TriageDecision.Kept ? "kept" : "discarded")}: {ex.Message}";
                    if (previous.Previous != TriageDecision.Discarded)
                    {
                        await OpenCurrentAsync(item).ConfigureAwait(false);
                    }

                    return;
                }

                item.Undo = result.Undo;
                if (result.Moved is { } moved)
                {
                    item.File = moved;
                }
            }

            item.Decision = decision;
            _history.Push(previous);
            LastError = null;
            RaiseCounts();
            advanceAfter = _options.AutoAdvanceDelay is not null;
            _advancePending = advanceAfter;
        }
        finally
        {
            _gate.Release();
        }

        if (advanceAfter)
        {
            // A wait before the next file must not hold up the key that asked for it: the call returns, and the move happens when the
            // clock says so (or sooner, when another key is pressed). No wait means the next file is open when the call returns.
            if (_options.AutoAdvanceDelay is { } delay && delay > TimeSpan.Zero)
            {
                _ = AdvanceAfterDelayAsync();
            }
            else
            {
                await AdvanceAfterDelayAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task AdvanceAfterDelayAsync()
    {
        var token = _navigation.Token;
        try
        {
            if (_options.AutoAdvanceDelay is { } delay && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, token).ConfigureAwait(false);
            }

            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // another command took over: it does its own advancing
        }

        try
        {
            if (_advancePending && !token.IsCancellationRequested)
            {
                _advancePending = false;
                await MoveNextCoreAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            LastError = ex.Message; // the player went away mid-advance; nothing else to do on a background step
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MoveNextCoreAsync()
    {
        var next = _index + 1;
        if (next >= _items.Count)
        {
            await _engine.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            Finish();
            return;
        }

        await GoToCoreAsync(next).ConfigureAwait(false);
    }

    // Moves to a file and opens it (a discarded file only shows its state: it is not where it was).
    private async Task GoToCoreAsync(int index)
    {
        _advancePending = false;
        _index = index;
        RaiseCurrent();
        var item = _items[index];
        if (!item.IsPlayable)
        {
            await _engine.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await OpenCurrentAsync(item).ConfigureAwait(false);
    }

    private async Task OpenCurrentAsync(TriageItem item)
    {
        var token = ResetNavigation();
        Keys.StopBurstSkim();
        try
        {
            await _engine.OpenAsync(item.File.Location.ToWin32Path(), TimeSpan.Zero, autoplay: true, token).ConfigureAwait(false);
            var snapshot = _engine.Snapshot;
            if (snapshot.State == PlaybackState.Failed)
            {
                LastError = $"{item.File.Name} could not be played: {snapshot.Error ?? "unknown error"}";
                return;
            }

            // Seeking once it plays is exact where a start option is only as good as the next keyframe; short clips start at the beginning.
            if (_options.StartOffset > 0 && snapshot.Duration >= _options.OffsetMinimumDuration)
            {
                await _engine.SeekAsync(snapshot.Duration * Math.Clamp(_options.StartOffset, 0, 0.9), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Moved on before it opened.
        }
    }

    private void Finish()
    {
        _finished = true;
        RaiseCurrent();
        RaiseCounts();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private int Count(TriageDecision decision) => _items.Count(i => i.Decision == decision);

    private CancellationToken ResetNavigation()
    {
        CancelNavigation();
        return _navigation.Token;
    }

    private void CancelNavigation()
    {
        _navigation.Cancel();
        _navigation.Dispose();
        _navigation = new CancellationTokenSource();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(Kept));
        OnPropertyChanged(nameof(Discarded));
        OnPropertyChanged(nameof(Skipped));
        OnPropertyChanged(nameof(ReclaimedBytes));
        OnPropertyChanged(nameof(ReclaimedText));
        OnPropertyChanged(nameof(ProgressText));
    }

    private void RaiseCurrent()
    {
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(ProgressText));
    }
}
