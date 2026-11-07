using Leviathan.Core.Model;

namespace Leviathan.ViewModels.Preview;

/// <summary>One preview to show: what it is, what it is of, and the token that is cancelled when the selection moves on.</summary>
/// <param name="Kind">The renderer.</param>
/// <param name="Entries">The selection (one item unless <see cref="Kind"/> is <see cref="PreviewKind.Multiple"/>).</param>
/// <param name="Cancellation">Cancelled when a newer selection arrives; a renderer must stop and drop what it has.</param>
public sealed record PreviewRequest(PreviewKind Kind, IReadOnlyList<FileEntry> Entries, CancellationToken Cancellation);

/// <summary>
/// Turns a stream of selection changes into previews (spec §9.1: "Previews load after the selection has rested for 150 ms, are
/// cancelled when the selection changes"). Holding the arrow key through a folder raises dozens of changes a second; only the one the
/// user stops on loads. Time goes through <see cref="TimeProvider"/> so the rest period is tested without waiting.
/// </summary>
public sealed class PreviewCoordinator : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _rest;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _current;
    private bool _disposed;

    /// <summary>Creates a coordinator that waits <paramref name="rest"/> (default <see cref="PreviewLimits.RestDelay"/>) before showing.</summary>
    public PreviewCoordinator(TimeProvider time, TimeSpan? rest = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _rest = rest ?? PreviewLimits.RestDelay;
    }

    /// <summary>
    /// Raised once the selection has rested, on a thread-pool thread: the window marshals it to the UI thread. The previous request's
    /// token is already cancelled by then.
    /// </summary>
    public event EventHandler<PreviewRequest>? Requested;

    /// <summary>Raised at once when the selection changes, before it has rested, so the pane can drop what it was showing.</summary>
    public event EventHandler? Moved;

    /// <summary>The selection changed: cancel what was loading and, if it stays put for the rest period, ask for its preview.</summary>
    public void Select(IReadOnlyList<FileEntry> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        CancellationTokenSource source;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            CancelCurrent();
            source = _current = new CancellationTokenSource();
        }

        Moved?.Invoke(this, EventArgs.Empty);
        var entries = selection.ToArray();
        _ = RestThenRaiseAsync(entries, source);
    }

    /// <summary>Cancels what is loading and shows nothing (the pane was hidden).</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            CancelCurrent();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            CancelCurrent();
        }
    }

    private void CancelCurrent()
    {
        if (_current is { } previous)
        {
            _current = null;
            previous.Cancel();
        }
    }

    private async Task RestThenRaiseAsync(FileEntry[] entries, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(_rest, _time, source.Token).ConfigureAwait(false);
            source.Token.ThrowIfCancellationRequested();
            Requested?.Invoke(this, new PreviewRequest(PreviewClassifier.Classify(entries), entries, source.Token));
        }
        catch (OperationCanceledException)
        {
            // A newer selection arrived while this one was resting: it has nothing to show.
        }
    }
}
