using Leviathan.Core.Model;
using Leviathan.Media;

namespace Leviathan.ViewModels.Folders;

/// <summary>One file to ask about: where it is and which version of it (spec §9.3).</summary>
/// <param name="Path">The Win32 path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ModifiedUtcTicks">Last write time, UTC ticks.</param>
/// <param name="IsNetwork">True on a share.</param>
public readonly record struct MediaFileRef(string Path, long Size, long ModifiedUtcTicks, bool IsNetwork);

/// <summary>What is known about one file's media information.</summary>
public enum MediaInfoState
{
    /// <summary>The metadata is in <see cref="MediaInfoResult.Info"/>.</summary>
    Ok,

    /// <summary>ffprobe could not read the file; <see cref="MediaInfoResult.Message"/> says why, and it is not tried again until the file changes.</summary>
    Unreadable,

    /// <summary>The media process cannot be used right now (not installed, disabled, or not answering); cells stay empty and the folder is not hammered.</summary>
    Unavailable,
}

/// <summary>The answer for one file.</summary>
/// <param name="State">What is known.</param>
/// <param name="Info">The metadata (<see cref="MediaInfoState.Ok"/> only).</param>
/// <param name="Message">The reason for the other states.</param>
public sealed record MediaInfoResult(MediaInfoState State, MediaInfo? Info, string? Message);

/// <summary>The call failed for a reason that may pass (the process died under it, a timeout); the tracker asks again, at most twice per file.</summary>
public sealed class MediaInfoTransientException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The source cannot answer at all (the process is disabled or not installed); the tracker stops asking for this folder.</summary>
public sealed class MediaInfoUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where media information comes from: the media process in the app, a fake in tests (spec §9.3, §19).</summary>
public interface IMediaInfoSource
{
    /// <summary>What is already cached for these files, in one round trip and without probing; files with no answer are left out.</summary>
    Task<IReadOnlyDictionary<string, MediaInfoResult>> PeekAsync(IReadOnlyList<MediaFileRef> files, CancellationToken cancellationToken);

    /// <summary>Reads one file (from the cache, or by probing it). Cancelling it cancels the probe if it has not started.</summary>
    /// <exception cref="MediaInfoUnavailableException">The media process cannot be used.</exception>
    Task<MediaInfoResult> ProbeAsync(MediaFileRef file, CancellationToken cancellationToken);
}

/// <summary>How far filling a whole folder has got (the progress shown in a media column header, spec §8.2).</summary>
/// <param name="Done">Files with an answer.</param>
/// <param name="Total">Media files in the folder.</param>
/// <param name="IsRunning">True while files are still being read for the fill.</param>
public readonly record struct MediaFillProgress(int Done, int Total, bool IsRunning)
{
    /// <summary>0 to 1, or null when there is nothing to fill.</summary>
    public double? Fraction => Total > 0 ? (double)Done / Total : null;
}

/// <summary>
/// Feeds the media columns (spec §8.2, §9.3): reads what the cache already knows for the rows on screen in one call, then asks for the
/// rest a few at a time, rows on screen first. A folder change cancels everything at once. Sorting by a media column asks for
/// <see cref="FillFolder"/>, which reads the whole folder behind the visible rows and reports progress. Used from the UI thread; the
/// probes run on the pool and their answers reach the UI in batches.
/// </summary>
public sealed class MediaInfoTracker : IDisposable
{
    /// <summary>How many files are asked about at once (the process itself allows two probes; a third waits there cheaply, so a little more keeps it busy).</summary>
    public const int MaxParallel = 3;

    /// <summary>How long answers are gathered before the view is told, so a burst of files is one update.</summary>
    public static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(80);

    private const int MaxAttempts = 2;

    private readonly IMediaInfoSource? _source;
    private readonly IUiContext _ui;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MediaInfoResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaFileRef> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _visible = [];
    private List<string> _fill = [];
    private CancellationTokenSource _generation = new();
    private int _running;
    private int _fillTotal;
    private bool _filling;
    private bool _unavailable;
    private bool _flushScheduled;
    private bool _progressDirty;
    private bool _disposed;

    /// <summary>Creates a tracker; with a null <paramref name="source"/> media columns simply stay empty.</summary>
    public MediaInfoTracker(IMediaInfoSource? source, IUiContext ui, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _source = source;
        _ui = ui;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on the UI thread when the information of these files changed.</summary>
    public event EventHandler<IReadOnlyList<string>>? Changed;

    /// <summary>Raised on the UI thread when <see cref="Progress"/> changed.</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>True when the source gave up on this folder (the media process is not usable); the columns show nothing and say so.</summary>
    public bool IsUnavailable
    {
        get
        {
            lock (_lock)
            {
                return _unavailable;
            }
        }
    }

    /// <summary>How far the whole-folder fill has got; <see cref="MediaFillProgress.IsRunning"/> is false when none was asked for or it finished.</summary>
    public MediaFillProgress Progress
    {
        get
        {
            lock (_lock)
            {
                if (!_filling)
                {
                    return default;
                }

                // A file that failed twice counts as settled, or one stubborn file would keep the progress running for ever.
                var settled = _fill.Count(p => _results.ContainsKey(p) || _attempts.GetValueOrDefault(p) >= MaxAttempts);
                return new MediaFillProgress(settled, _fillTotal, settled < _fillTotal);
            }
        }
    }

    /// <summary>The answer for <paramref name="entry"/>, or null when it is not a media file, or nothing is known yet.</summary>
    public MediaInfoResult? TryGet(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            return _results.GetValueOrDefault(entry.Location.ToWin32Path());
        }
    }

    /// <summary>True when <paramref name="entry"/> is a file that media columns apply to (judged by extension).</summary>
    public static bool IsMedia(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind == FileEntryKind.File && MediaFileKinds.IsMedia(entry.Extension);
    }

    /// <summary>
    /// A different folder is shown: everything in flight is cancelled, nothing is carried over, and the process is tried again even if it
    /// was unavailable (it may have been installed or started meanwhile).
    /// </summary>
    public void SetFolder()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _generation;
            _generation = new CancellationTokenSource();
            _results.Clear();
            _known.Clear();
            _attempts.Clear();
            _inFlight.Clear();
            _dirty.Clear();
            _visible = [];
            _fill = [];
            _fillTotal = 0;
            _filling = false;
            _unavailable = false;
        }

        old.Cancel();
        old.Dispose();
        RaiseProgress();
    }

    /// <summary>
    /// The rows on screen (reported by the grid after it has settled, spec §3.2). Their cached answers are read in one call, the rest are
    /// asked for first; rows that scrolled away lose their place in line (a probe already running is left to finish and is cached).
    /// </summary>
    public void SetVisible(IReadOnlyList<FileEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_source is null || _disposed)
        {
            return;
        }

        var media = Refs(rows);
        CancellationToken generation;
        List<MediaFileRef> toPeek;
        lock (_lock)
        {
            if (_unavailable)
            {
                return;
            }

            generation = _generation.Token;
            foreach (var file in media)
            {
                _known[file.Path] = file;
            }

            _visible = [.. media.Select(m => m.Path)];
            toPeek = [.. media.Where(m => !_results.ContainsKey(m.Path))];
        }

        if (toPeek.Count == 0)
        {
            Pump();
            return;
        }

        _ = Task.Run(() => PeekThenPumpAsync(toPeek, generation), CancellationToken.None);
    }

    /// <summary>
    /// Reads the whole folder, behind the rows on screen, at low priority (sorting by a media column needs every value). Progress is in
    /// <see cref="Progress"/>. Calling it again with the same entries only keeps it going.
    /// </summary>
    public void FillFolder(IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (_source is null || _disposed)
        {
            return;
        }

        var media = Refs(entries);
        lock (_lock)
        {
            if (_unavailable)
            {
                return;
            }

            foreach (var file in media)
            {
                _known[file.Path] = file;
            }

            _fill = [.. media.Select(m => m.Path)];
            _fillTotal = _fill.Count;
            _filling = _fillTotal > 0;
        }

        RaiseProgress();
        var generation = CurrentGeneration();
        _ = Task.Run(() => PeekThenPumpAsync([.. media], generation), CancellationToken.None);
    }

    /// <summary>Stops a whole-folder fill (the column is no longer sorted by); answers already in stay.</summary>
    public void CancelFill()
    {
        lock (_lock)
        {
            _fill = [];
            _fillTotal = 0;
            _filling = false;
        }

        RaiseProgress();
    }

    /// <summary>Stops everything.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation.Cancel();
        _generation.Dispose();
    }

    private static List<MediaFileRef> Refs(IReadOnlyList<FileEntry> entries) =>
    [
        .. entries.Where(IsMedia).Select(e => new MediaFileRef(e.Location.ToWin32Path(), e.Size, e.Modified.UtcDateTime.Ticks, e.Location.IsNetwork)),
    ];

    private CancellationToken CurrentGeneration()
    {
        lock (_lock)
        {
            return _generation.Token;
        }
    }

    private async Task PeekThenPumpAsync(List<MediaFileRef> files, CancellationToken generation)
    {
        try
        {
            var cached = await _source!.PeekAsync(files, generation).ConfigureAwait(false);
            if (generation.IsCancellationRequested)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var (path, result) in cached)
                {
                    if (!generation.IsCancellationRequested)
                    {
                        _results[path] = result;
                        _dirty.Add(path);
                    }
                }
            }

            ScheduleFlush();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is MediaInfoUnavailableException or MediaInfoTransientException or IOException)
        {
            // The cache lookup failed; the probes below will say whether the process is usable.
        }

        Pump();
    }

    // Starts probes until MaxParallel are running: rows on screen first, then the whole-folder fill, each in listing order.
    private void Pump()
    {
        while (true)
        {
            MediaFileRef file;
            CancellationToken generation;
            lock (_lock)
            {
                if (_disposed || _unavailable || _running >= MaxParallel || NextWork() is not { } next)
                {
                    return;
                }

                file = next;
                generation = _generation.Token;
                _inFlight.Add(file.Path);
                _running++;
            }

            _ = Task.Run(() => ProbeAsync(file, generation), CancellationToken.None);
        }
    }

    private MediaFileRef? NextWork()
    {
        foreach (var path in _visible)
        {
            if (NeedsProbe(path))
            {
                return _known[path];
            }
        }

        if (_filling)
        {
            foreach (var path in _fill)
            {
                if (NeedsProbe(path))
                {
                    return _known[path];
                }
            }
        }

        return null;
    }

    private bool NeedsProbe(string path) =>
        !_results.ContainsKey(path) && !_inFlight.Contains(path) && _known.ContainsKey(path) && _attempts.GetValueOrDefault(path) < MaxAttempts;

    private async Task ProbeAsync(MediaFileRef file, CancellationToken generation)
    {
        MediaInfoResult? result = null;
        try
        {
            result = await _source!.ProbeAsync(file, generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Folder changed or the row scrolled away before it started.
        }
        catch (MediaInfoUnavailableException ex)
        {
            result = new MediaInfoResult(MediaInfoState.Unavailable, null, ex.Message);
            lock (_lock)
            {
                if (!generation.IsCancellationRequested)
                {
                    _unavailable = true;
                }
            }
        }
        catch (Exception ex) when (ex is MediaInfoTransientException or IOException or TimeoutException)
        {
            // The process died under this call: try again (it is started again by the next call), but never more than twice for a file.
            lock (_lock)
            {
                _attempts[file.Path] = _attempts.GetValueOrDefault(file.Path) + 1;
            }
        }

        lock (_lock)
        {
            _inFlight.Remove(file.Path);
            _running--;
            _progressDirty = _filling;
            if (result is not null && !generation.IsCancellationRequested)
            {
                _results[file.Path] = result;
                _dirty.Add(file.Path);
            }
        }

        if (!generation.IsCancellationRequested)
        {
            ScheduleFlush();
            Pump();
        }
    }

    // Answers are told to the view at most every FlushEvery, never one by one (spec §3.2).
    private void ScheduleFlush()
    {
        lock (_lock)
        {
            if (_flushScheduled || _disposed)
            {
                return;
            }

            _flushScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(FlushEvery, _time).ConfigureAwait(false);
            IReadOnlyList<string> changed;
            bool progress;
            lock (_lock)
            {
                changed = [.. _dirty];
                progress = _progressDirty || changed.Count > 0;
                _dirty.Clear();
                _progressDirty = false;
                _flushScheduled = false;
            }

            if (progress && !_disposed)
            {
                _ui.Post(() =>
                {
                    if (changed.Count > 0)
                    {
                        Changed?.Invoke(this, changed);
                    }

                    ProgressChanged?.Invoke(this, EventArgs.Empty);
                });
            }
        });
    }

    private void RaiseProgress() => _ui.Post(() => ProgressChanged?.Invoke(this, EventArgs.Empty));
}
