using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Folders;

/// <summary>Runs work on the UI thread. The WinUI app wraps its <c>DispatcherQueue</c>; tests run work inline.</summary>
public interface IUiContext
{
    /// <summary>Queues <paramref name="action"/> on the UI thread, in order. Must not block the caller.</summary>
    void Post(Action action);
}

/// <summary>Where a folder listing stands (spec §8.6).</summary>
public enum FolderLoadState
{
    /// <summary>Nothing requested yet.</summary>
    Idle,

    /// <summary>Listing in progress; rows arrive in batches.</summary>
    Loading,

    /// <summary>Listing finished and the folder has items.</summary>
    Loaded,

    /// <summary>Listing finished and the folder is empty.</summary>
    Empty,

    /// <summary>The listing was abandoned (cancelled). Rows shown so far are partial. Never reported as <see cref="Empty"/>.</summary>
    Cancelled,

    /// <summary>The folder does not exist.</summary>
    NotFound,

    /// <summary>Access was denied.</summary>
    AccessDenied,

    /// <summary>The server or share is not responding, timed out or needs credentials (spec §3.3).</summary>
    Offline,

    /// <summary>Any other failure.</summary>
    Error,
}

/// <summary>An observable list that can append many items with a single change notification.</summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Appends <paramref name="items"/> and raises one <see cref="NotifyCollectionChangedAction.Add"/> event.</summary>
    public void AddRange(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        var start = Items.Count;
        var added = new List<T>(items);
        foreach (var item in added)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
    }
}

/// <summary>
/// Lists one folder for a tab. Batches stream in from the provider and reach the UI as one change per batch (spec §3.2),
/// navigating away cancels the old listing and its late batches are dropped, and a cancelled or failed listing is never
/// shown as an empty folder (spec §8.6, CLAUDE.md).
/// </summary>
public sealed partial class FolderViewModel : ObservableObject, IDisposable
{
    private readonly LocationProviderRegistry _providers;
    private readonly IUiContext _ui;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _current;
    private int _generation;

    [ObservableProperty]
    private LocationPath? _location;

    [ObservableProperty]
    private FolderLoadState _state = FolderLoadState.Idle;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isRetryable;

    [ObservableProperty]
    private FolderKind _kind = FolderKind.General;

    /// <summary>Creates the view model.</summary>
    public FolderViewModel(LocationProviderRegistry providers, IUiContext ui, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(ui);
        _providers = providers;
        _ui = ui;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Items listed so far, in arrival order. Sorting, grouping and filtering sit on top of this list.</summary>
    public RangeObservableCollection<FileEntry> Entries { get; } = [];

    /// <summary>Hidden and system item visibility; takes effect on the next load.</summary>
    public ListingOptions Options { get; set; } = ListingOptions.Default;

    /// <summary>Time from the start of the last load to its first rows reaching the UI queue (the §3.1 budget).</summary>
    public TimeSpan? FirstRowsElapsed { get; private set; }

    /// <summary>Time from the start of the last load to its end.</summary>
    public TimeSpan? TotalElapsed { get; private set; }

    /// <summary>Lists <paramref name="location"/>, cancelling any listing in progress. Completes when the listing ends.</summary>
    public async Task LoadAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var generation = Interlocked.Increment(ref _generation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _current, source);
        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(false);
            previous.Dispose();
        }

        var options = Options;
        var started = _time.GetTimestamp();
        FirstRowsElapsed = null;
        TotalElapsed = null;
        var all = new List<FileEntry>();

        PostIfCurrent(generation, () =>
        {
            Location = location;
            Entries.Clear();
            Message = null;
            IsRetryable = false;
            Kind = FolderKind.General;
            State = FolderLoadState.Loading;
        });

        try
        {
            var provider = _providers.Resolve(location);
            await foreach (var batch in provider.ListAsync(location, options, source.Token).ConfigureAwait(false))
            {
                if (batch.Items.Count > 0)
                {
                    FirstRowsElapsed ??= _time.GetElapsedTime(started);
                    all.AddRange(batch.Items);
                    var items = batch.Items;
                    PostIfCurrent(generation, () => Entries.AddRange(items));
                }
            }

            TotalElapsed = _time.GetElapsedTime(started);
            var kind = FolderKindDetector.Detect(all);
            PostIfCurrent(generation, () =>
            {
                Kind = kind;
                State = all.Count == 0 ? FolderLoadState.Empty : FolderLoadState.Loaded;
            });
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // A newer load already reset the state; only the load that was cancelled without a replacement reports it.
            PostIfCurrent(generation, () => State = FolderLoadState.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failure = ex as LocationException ?? LocationErrors.Wrap(ex, location);
            PostIfCurrent(generation, () => ShowFailure(failure));
        }
    }

    /// <summary>Lists the current folder again (F5).</summary>
    public Task ReloadAsync(CancellationToken cancellationToken) =>
        Location is null ? Task.CompletedTask : LoadAsync(Location, cancellationToken);

    /// <summary>Cancels the listing in progress, if any. The state becomes <see cref="FolderLoadState.Cancelled"/>.</summary>
    public void Cancel() => _current?.Cancel();

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        var source = Interlocked.Exchange(ref _current, null);
        source?.Cancel();
        source?.Dispose();
    }

    private void ShowFailure(LocationException failure)
    {
        Message = failure.Message;
        IsRetryable = failure.IsTransient || failure.Error == LocationError.AuthenticationRequired;
        State = failure.Error switch
        {
            LocationError.NotFound => FolderLoadState.NotFound,
            LocationError.AccessDenied => FolderLoadState.AccessDenied,
            LocationError.Unreachable or LocationError.Timeout or LocationError.AuthenticationRequired => FolderLoadState.Offline,
            _ => FolderLoadState.Error,
        };
    }

    private void PostIfCurrent(int generation, Action action) =>
        _ui.Post(() =>
        {
            if (Volatile.Read(ref _generation) == generation)
            {
                action();
            }
        });
}
