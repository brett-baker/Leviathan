using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>Where the facts about decided files are kept. Derived data: it can always be collected again, so a damaged store is simply started over.</summary>
public interface IFileFactStore
{
    /// <summary>The facts for a file, or null when none are kept.</summary>
    Task<FileFacts?> GetAsync(string path, CancellationToken cancellationToken);

    /// <summary>Keeps the facts, replacing any earlier ones for the same path.</summary>
    Task SaveAsync(FileFacts facts, CancellationToken cancellationToken);

    /// <summary>How many files have complete facts, and how many are waiting for a server.</summary>
    Task<(int Complete, int Incomplete)> CountAsync(CancellationToken cancellationToken);

    /// <summary>The paths of the files whose facts are complete (cheap: no facts are read).</summary>
    Task<ISet<string>> CompletePathsAsync(CancellationToken cancellationToken);

    /// <summary>Every file's facts (for the model).</summary>
    Task<IReadOnlyList<FileFacts>> LoadAllAsync(CancellationToken cancellationToken);
}

/// <summary>Reads the video facts of a file from the media process (or its cache). The file may be gone by now: then null.</summary>
public interface IVideoFactsSource
{
    /// <summary>The facts, or null when they cannot be had (file gone, not readable, ffprobe missing).</summary>
    Task<VideoFacts?> GetAsync(string path, long size, long modifiedTicks, CancellationToken cancellationToken);
}

/// <summary>
/// Collects, in the background, what the learning needs to know about each file the user decided (triage learning plan, slice 3): what
/// the name says, what ffprobe says and what each enabled server knows. One file at a time, off the UI thread, cancellable. A file is
/// asked about the moment it is decided, while it can still be read; a server that cannot be reached leaves the file incomplete, to be
/// tried again on the next <see cref="BackfillAsync"/>. Nothing here ever changes a server, and nothing stops the triage.
/// </summary>
public sealed class FeatureCollector
{
    private readonly IFileFactStore _store;
    private readonly IVideoFactsSource _video;
    private readonly ISceneLookup _scenes;
    private readonly Func<LearningSettings> _settings;
    private readonly ILearningSecrets _secrets;
    private readonly ITriageHistory _history;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Queue<TriageDecisionRecord> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private Task _worker = Task.CompletedTask;
    private int _pending;

    /// <summary>Creates the collector; nothing runs until a decision is queued.</summary>
    public FeatureCollector(IFileFactStore store, IVideoFactsSource video, ISceneLookup scenes, Func<LearningSettings> settings, ILearningSecrets secrets, ITriageHistory history, TimeProvider? time = null)
    {
        _store = store;
        _video = video;
        _scenes = scenes;
        _settings = settings;
        _secrets = secrets;
        _history = history;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised (on a pool thread) when a file has been collected or the number waiting changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Files queued or being collected.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Completes when the queue is empty (for tests and for shutting down cleanly).</summary>
    public Task Idle
    {
        get
        {
            lock (_lock)
            {
                return _worker;
            }
        }
    }

    /// <summary>Queues a decided file. Does nothing while learning is off, and ignores skips and taken-back decisions.</summary>
    public void Enqueue(TriageDecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_settings().Enabled || record.Decision is not (TriageDecision.Kept or TriageDecision.Discarded))
        {
            return;
        }

        lock (_lock)
        {
            if (!_queued.Add(record.Path))
            {
                return;
            }

            _queue.Enqueue(record);
            Interlocked.Increment(ref _pending);
            if (_worker.IsCompleted)
            {
                _worker = Task.Run(RunAsync);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Queues every file the history holds an example for that has no complete facts yet: after a start-up, after learning was switched
    /// on, and for files whose server was unreachable the first time. Returns how many were queued.
    /// </summary>
    public async Task<int> BackfillAsync(CancellationToken cancellationToken)
    {
        if (!_settings().Enabled)
        {
            return 0;
        }

        var queued = 0;
        var active = _settings().Active.ToList();
        foreach (var example in TriageHistoryReplay.Examples(await _history.LoadAsync(cancellationToken).ConfigureAwait(false)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var facts = await _store.GetAsync(example.Path, cancellationToken).ConfigureAwait(false);
            if (facts is { Complete: true } && active.All(s => facts.Asked.Contains(s.Id)))
            {
                continue;
            }

            Enqueue(example);
            queued++;
        }

        return queued;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            TriageDecisionRecord record;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out record!))
                {
                    return;
                }
            }

            try
            {
                await CollectAsync(record).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The store could not be written: the file stays uncollected and is queued again by the next backfill.
            }
            finally
            {
                lock (_lock)
                {
                    _queued.Remove(record.Path);
                }

                Interlocked.Decrement(ref _pending);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task CollectAsync(TriageDecisionRecord record)
    {
        var stored = await _store.GetAsync(record.Path, CancellationToken.None).ConfigureAwait(false);
        var facts = await GatherAsync(record.Path, record.Size, record.ModifiedTicks, stored, CancellationToken.None).ConfigureAwait(false);
        await _store.SaveAsync(facts, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Works out what is known about a file now, adding to <paramref name="stored"/> when some facts were already kept. Nothing is saved:
    /// the hint uses this for the file on screen, the collector saves it for a decided one.
    /// </summary>
    public async Task<FileFacts> GatherAsync(string path, long size, long modifiedTicks, FileFacts? stored, CancellationToken cancellationToken)
    {
        var video = stored?.Video ?? await SafeVideoAsync(path, size, modifiedTicks, cancellationToken).ConfigureAwait(false);
        var scenes = new List<SceneFacts>(stored?.Scenes ?? []);
        var asked = new List<string>(stored?.Asked ?? []);
        var active = _settings().Active.ToList();
        foreach (var source in active.Where(s => !asked.Contains(s.Id)))
        {
            var key = await Task.Run(() => _secrets.Get(source.Id), cancellationToken).ConfigureAwait(false);
            var result = await _scenes.FindAsync(source, key, path, size, cancellationToken).ConfigureAwait(false);
            if (result.Reached)
            {
                asked.Add(source.Id);
                if (result.Scene is not null)
                {
                    scenes.Add(result.Scene);
                }
            }
        }

        var complete = active.All(s => asked.Contains(s.Id));
        return new FileFacts(path, size, modifiedTicks, _time.GetUtcNow(), NameParser.Parse(path), video, scenes, asked, complete);
    }

    private async Task<VideoFacts?> SafeVideoAsync(string path, long size, long modifiedTicks, CancellationToken cancellationToken)
    {
        try
        {
            return await _video.GetAsync(path, size, modifiedTicks, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }
}
