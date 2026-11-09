using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tags;

/// <summary>
/// The colour tags of the files in the folder on show (spec §11.1, §11.2 rule 3): reads the tags of the rows on screen first, a few
/// at a time off the UI thread, and tells the view in batches. Files outside the screen are read only when a <c>tag:</c> filter needs
/// them (<see cref="LoadAll"/>). A different folder cancels everything. A file whose tags cannot be read (a location that refuses,
/// a file that vanished) counts as untagged and is not asked again, so one bad file never keeps the folder busy. It is also the
/// <see cref="IEntryMetadata"/> that the filter box uses for <c>tag:</c>. Used from the UI thread.
/// </summary>
public sealed class FolderTagTracker : IEntryMetadata, IDisposable
{
    /// <summary>How many files are read at once.</summary>
    public const int MaxParallel = 4;

    /// <summary>How long answers are gathered before the view is told.</summary>
    public static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(80);

    private readonly ITagStore? _store;
    private readonly IUiContext _ui;
    private readonly Func<TagCatalog> _catalog;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IReadOnlySet<int>> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _visible = [];
    private List<string> _rest = [];
    private CancellationTokenSource _generation = new();
    private int _running;
    private bool _flushScheduled;
    private bool _disposed;

    /// <summary>Creates a tracker; with a null <paramref name="store"/> no file ever shows a tag.</summary>
    public FolderTagTracker(ITagStore? store, IUiContext ui, Func<TagCatalog>? catalog = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _store = store;
        _ui = ui;
        _catalog = catalog ?? (() => TagCatalog.Default);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on the UI thread when the tags of these files (Win32 paths) became known.</summary>
    public event EventHandler<IReadOnlyList<string>>? Changed;

    /// <summary>True when the tags of <paramref name="entry"/> have been read.</summary>
    public bool IsKnown(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            return _results.ContainsKey(entry.Location.ToWin32Path());
        }
    }

    /// <summary>The tags of <paramref name="entry"/> in the user's order; empty when it has none or they are not read yet.</summary>
    public IReadOnlyList<ColorTag> TagsOf(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        IReadOnlySet<int>? ids;
        lock (_lock)
        {
            _results.TryGetValue(entry.Location.ToWin32Path(), out ids);
        }

        return ids is null || ids.Count == 0 ? [] : [.. _catalog().Tags.Where(t => ids.Contains(t.Id))];
    }

    /// <inheritdoc />
    public IReadOnlySet<string>? GetTags(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsKnown(entry))
        {
            return null;
        }

        return TagsOf(entry).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public TimeSpan? GetDuration(FileEntry entry) => null;

    /// <summary>A different folder is shown: everything in flight is cancelled and nothing is carried over.</summary>
    public void SetFolder()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _generation;
            _generation = new CancellationTokenSource();
            _results.Clear();
            _inFlight.Clear();
            _dirty.Clear();
            _visible = [];
            _rest = [];
        }

        old.Cancel();
        old.Dispose();
    }

    /// <summary>Forgets what is known about these files so they are read again (a tag was just changed on them).</summary>
    public void Invalidate(IEnumerable<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                _results.Remove(entry.Location.ToWin32Path());
            }
        }
    }

    /// <summary>Reads these files again, first in line (their tags were just changed here).</summary>
    public void Reload(IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Invalidate(entries);
        lock (_lock)
        {
            var paths = entries.Where(IsTaggable).Select(e => e.Location.ToWin32Path()).ToList();
            _visible = [.. paths, .. _visible.Except(paths, StringComparer.OrdinalIgnoreCase)];
        }

        Pump();
    }

    /// <summary>The rows on screen: read first. Rows that scrolled away lose their place in line.</summary>
    public void SetVisible(IReadOnlyList<FileEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_lock)
        {
            _visible = [.. rows.Where(IsTaggable).Select(e => e.Location.ToWin32Path())];
        }

        Pump();
    }

    /// <summary>Reads the whole folder behind the rows on screen (a <c>tag:</c> filter needs every file).</summary>
    public void LoadAll(IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_lock)
        {
            _rest = [.. entries.Where(IsTaggable).Select(e => e.Location.ToWin32Path())];
        }

        Pump();
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

    private static bool IsTaggable(FileEntry entry) => entry.Kind == FileEntryKind.File;

    private void Pump()
    {
        if (_store is null)
        {
            return;
        }

        while (true)
        {
            string path;
            CancellationToken generation;
            lock (_lock)
            {
                if (_disposed || _running >= MaxParallel || Next() is not { } next)
                {
                    return;
                }

                path = next;
                generation = _generation.Token;
                _inFlight.Add(path);
                _running++;
            }

            _ = Task.Run(() => ReadAsync(path, generation), CancellationToken.None);
        }
    }

    private string? Next()
    {
        foreach (var path in _visible)
        {
            if (Needs(path))
            {
                return path;
            }
        }

        foreach (var path in _rest)
        {
            if (Needs(path))
            {
                return path;
            }
        }

        return null;
    }

    private bool Needs(string path) => !_results.ContainsKey(path) && !_inFlight.Contains(path);

    private async Task ReadAsync(string path, CancellationToken generation)
    {
        IReadOnlySet<int>? tags = null;
        try
        {
            tags = await _store!.GetAsync(path, generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The folder changed.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tags = new HashSet<int>(); // unreadable counts as untagged, and is not asked again
        }

        lock (_lock)
        {
            _inFlight.Remove(path);
            _running--;
            if (tags is not null && !generation.IsCancellationRequested)
            {
                _results[path] = tags;
                _dirty.Add(path);
            }
        }

        if (!generation.IsCancellationRequested)
        {
            ScheduleFlush();
            Pump();
        }
    }

    // Answers reach the view at most every FlushEvery, never one by one (spec §3.2).
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
            lock (_lock)
            {
                changed = [.. _dirty];
                _dirty.Clear();
                _flushScheduled = false;
            }

            if (changed.Count > 0 && !_disposed)
            {
                _ui.Post(() => Changed?.Invoke(this, changed));
            }
        });
    }
}
