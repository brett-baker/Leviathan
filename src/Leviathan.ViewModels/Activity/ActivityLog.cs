using System.Text;
using System.Text.Json;
using Leviathan.Core.Paths;
using Leviathan.Diagnostics;

namespace Leviathan.ViewModels.Activity;

/// <summary>What sort of thing an activity entry is, for the filter (spec §16.1).</summary>
public enum ActivityKind
{
    /// <summary>Went to a folder, back, forward, up, reloaded.</summary>
    Navigation,

    /// <summary>Opened, renamed, created, cut, copied, pasted, moved, deleted, undone, tagged.</summary>
    Files,

    /// <summary>Tabs, tab groups and windows.</summary>
    Tabs,

    /// <summary>View, sort, group, filter, columns, panes.</summary>
    View,

    /// <summary>Favorites, profiles and other settings.</summary>
    Settings,

    /// <summary>Something the app told the user: a finished or failed copy, an error shown.</summary>
    Message,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>One line of the session activity log (D35).</summary>
/// <param name="Id">Increasing number, so entries of one session keep their order.</param>
/// <param name="Time">When it happened (UTC).</param>
/// <param name="Session">The run of the app it belongs to.</param>
/// <param name="Kind">Filter group.</param>
/// <param name="Command">The command id, or <c>message</c> for something the app said.</param>
/// <param name="Summary">What happened in words.</param>
/// <param name="Target">What it acted on (a path, a name), or null.</param>
/// <param name="Location">A folder or item the entry can jump to, or null.</param>
/// <param name="IsError">True for a failure shown to the user.</param>
/// <param name="JobId">The copy, move or delete the entry is about; clicking the row opens that job's report (spec §16.1), or null.</param>
public sealed record ActivityEntry(long Id, DateTimeOffset Time, Guid Session, ActivityKind Kind, string Command, string Summary, string? Target, string? Location, bool IsError = false, Guid? JobId = null);

/// <summary>What the Activity tab shows: a kind, some text, and whether earlier sessions are included.</summary>
/// <param name="Kind">Only this kind, or null for all.</param>
/// <param name="Text">Only entries containing this text (summary, target or command), or empty.</param>
/// <param name="IncludeEarlier">Include the sessions kept from before this run (7 days).</param>
public sealed record ActivityQuery(ActivityKind? Kind = null, string? Text = null, bool IncludeEarlier = false);

/// <summary>Where the log is kept between runs (spec §16.1: earlier sessions are kept for 7 days).</summary>
public interface IActivityStore
{
    /// <summary>Adds entries.</summary>
    Task AppendAsync(IReadOnlyList<ActivityEntry> entries, CancellationToken cancellationToken);

    /// <summary>The entries since <paramref name="since"/>, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ActivityEntry>> LoadAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken);

    /// <summary>Forgets entries older than <paramref name="before"/>.</summary>
    Task PruneAsync(DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>Forgets everything.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The session activity log (spec §16.1, D35): every command the user ran, plus what the app told them (finished copies, errors),
/// newest first, searchable, kept in SQLite for seven days. Built on the command layer's <see cref="ActionTrail"/>. Adding costs a
/// lock and a list insert: the store is written in batches a second later on a thread-pool thread, never on the caller's thread
/// (spec §3.2). File names stay on this machine. Thread-safe.
/// </summary>
public sealed class ActivityLog : IDisposable
{
    /// <summary>How long entries of earlier sessions are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>The most entries held in memory (this session and the loaded earlier ones).</summary>
    public const int MemoryLimit = 5000;

    private static readonly TimeSpan FlushAfter = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly List<ActivityEntry> _entries = [];
    private readonly List<ActivityEntry> _pending = [];
    private readonly IActivityStore? _store;
    private readonly TimeProvider _time;
    private ITimer? _flushTimer;
    private long _next;
    private bool _earlierLoaded;
    private bool _disposed;

    /// <summary>Creates the log.</summary>
    /// <param name="store">Where entries are kept between runs, or null for this run only.</param>
    /// <param name="time">Clock.</param>
    /// <param name="session">The id of this run (a new one when null).</param>
    public ActivityLog(IActivityStore? store = null, TimeProvider? time = null, Guid? session = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        Session = session ?? Guid.NewGuid();
    }

    /// <summary>The id of this run.</summary>
    public Guid Session { get; }

    /// <summary>Raised after an entry was added or earlier sessions were loaded, on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Takes what the command layer recorded.</summary>
    public void Add(UserAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (ActivitySummary.IsNoise(action.Command))
        {
            return;
        }

        var (kind, summary, location) = ActivitySummary.Describe(action.Command, action.Target);
        Append(new ActivityEntry(0, action.Timestamp, Session, kind, action.Command, summary, action.Target, location));
    }

    /// <summary>Records something the app did or said (a finished or failed copy, an error), with a place to jump to.</summary>
    public void Note(string summary, ActivityKind kind = ActivityKind.Message, string? location = null, bool isError = false, Guid? jobId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        Append(new ActivityEntry(0, _time.GetUtcNow(), Session, kind, "message", summary, null, location, isError, jobId));
    }

    /// <summary>
    /// Loads the entries of the last seven days from earlier runs, once (the Activity tab does it when first shown, so startup
    /// never waits for it). Returns how many were added.
    /// </summary>
    public async Task<int> LoadEarlierAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_earlierLoaded)
            {
                return 0;
            }

            _earlierLoaded = true;
        }

        IReadOnlyList<ActivityEntry> earlier;
        try
        {
            var since = _time.GetUtcNow() - Retention;
            earlier = await Task.Run(() => _store.LoadAsync(since, MemoryLimit, cancellationToken), cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => PruneQuietlyAsync(since), CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return 0;
        }

        var added = 0;
        lock (_gate)
        {
            foreach (var entry in earlier.Where(e => e.Session != Session))
            {
                _entries.Add(entry with { Id = -1 - added++ });
            }

            _entries.Sort(static (a, b) => b.Time.CompareTo(a.Time));
            Trim();
        }

        if (added > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    /// <summary>The entries that match <paramref name="query"/>, newest first.</summary>
    public IReadOnlyList<ActivityEntry> Find(ActivityQuery? query = null)
    {
        query ??= new ActivityQuery();
        var text = query.Text?.Trim();
        lock (_gate)
        {
            return
            [
                .. _entries.Where(e =>
                    (query.IncludeEarlier || e.Session == Session)
                    && (query.Kind is null || e.Kind == query.Kind)
                    && (string.IsNullOrEmpty(text)
                        || e.Summary.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || (e.Target?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                        || e.Command.Contains(text, StringComparison.OrdinalIgnoreCase))),
            ];
        }
    }

    /// <summary>Forgets the whole log, here and in the store.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _entries.Clear();
            _pending.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (_store is not null)
        {
            try
            {
                await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The memory copy is gone; the file is cleared the next time.
            }
        }
    }

    /// <summary>The entries as plain text, one per line, oldest first: <c>2026-11-03 14:02:11  Opened C:\media</c>.</summary>
    public static string ExportText(IEnumerable<ActivityEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = new StringBuilder();
        foreach (var entry in entries.OrderBy(e => e.Time))
        {
            text.Append(entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                .Append("  ")
                .Append(entry.IsError ? "[error] " : string.Empty)
                .Append(entry.Summary);
            if (entry.Target is { Length: > 0 } && !entry.Summary.Contains(entry.Target, StringComparison.Ordinal))
            {
                text.Append("  (").Append(entry.Target).Append(')');
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>The entries as JSON, oldest first, for a file or another tool.</summary>
    public static string ExportJson(IEnumerable<ActivityEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = entries.OrderBy(e => e.Time).Select(e => new
        {
            time = e.Time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            kind = e.Kind.ToString(),
            command = e.Command,
            summary = e.Summary,
            target = e.Target,
            location = e.Location,
            error = e.IsError,
        });
        return JsonSerializer.Serialize(rows, ExportOptions);
    }

    /// <summary>Writes what is still waiting for the store, then stops.</summary>
    public void Dispose()
    {
        List<ActivityEntry> rest;
        lock (_gate)
        {
            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
            rest = [.. _pending];
            _pending.Clear();
        }

        if (rest.Count > 0 && _store is not null)
        {
            try
            {
                _store.AppendAsync(rest, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Closing: nothing more can be done.
            }
        }
    }

    private void Append(ActivityEntry entry)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var numbered = entry with { Id = ++_next };
            _entries.Insert(0, numbered);
            Trim();
            if (_store is not null)
            {
                _pending.Add(numbered);
                _flushTimer ??= _time.CreateTimer(_ => Flush(), null, FlushAfter, Timeout.InfiniteTimeSpan);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Trim()
    {
        if (_entries.Count > MemoryLimit)
        {
            _entries.RemoveRange(MemoryLimit, _entries.Count - MemoryLimit);
        }
    }

    private void Flush()
    {
        List<ActivityEntry> batch;
        lock (_gate)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            batch = [.. _pending];
            _pending.Clear();
        }

        if (batch.Count > 0 && _store is not null)
        {
            _ = WriteQuietlyAsync(batch);
        }
    }

    private async Task WriteQuietlyAsync(List<ActivityEntry> batch)
    {
        try
        {
            await _store!.AppendAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The entries stay in memory for this run; the log is a convenience, never a reason to fail an action.
        }
    }

    private async Task PruneQuietlyAsync(DateTimeOffset before)
    {
        try
        {
            await _store!.PruneAsync(before, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruned on a later run.
        }
    }
}

/// <summary>Turns a command id and its target into the line the Activity tab shows (spec §16.1).</summary>
public static class ActivitySummary
{
    private static readonly Dictionary<string, (ActivityKind Kind, string Text)> Known = new(StringComparer.Ordinal)
    {
        ["nav.back"] = (ActivityKind.Navigation, "Went back"),
        ["nav.forward"] = (ActivityKind.Navigation, "Went forward"),
        ["nav.up"] = (ActivityKind.Navigation, "Went up one folder"),
        ["nav.reload"] = (ActivityKind.Navigation, "Reloaded the folder"),
        ["nav.go"] = (ActivityKind.Navigation, "Went to"),
        ["sidebar.open"] = (ActivityKind.Navigation, "Opened from the sidebar"),
        ["address.focus"] = (ActivityKind.Navigation, "Edited the address"),
        ["item.open"] = (ActivityKind.Files, "Opened"),
        ["item.drop"] = (ActivityKind.Files, "Dropped items on"),
        ["item.properties"] = (ActivityKind.Files, "Showed properties of"),
        ["item.preview"] = (ActivityKind.Files, "Previewed"),
        ["item.print"] = (ActivityKind.Files, "Printed"),
        ["item.checksum"] = (ActivityKind.Files, "Showed the checksum of"),
        ["item.compare"] = (ActivityKind.Files, "Compared"),
        ["item.mount"] = (ActivityKind.Files, "Mounted"),
        ["item.burn"] = (ActivityKind.Files, "Burned"),
        ["new.folder"] = (ActivityKind.Files, "Created a folder in"),
        ["new.item"] = (ActivityKind.Files, "Created a new item in"),
        ["edit.cut"] = (ActivityKind.Files, "Cut"),
        ["edit.copy"] = (ActivityKind.Files, "Copied"),
        ["edit.paste"] = (ActivityKind.Files, "Pasted into"),
        ["edit.rename"] = (ActivityKind.Files, "Renamed"),
        ["edit.delete"] = (ActivityKind.Files, "Deleted"),
        ["edit.undo"] = (ActivityKind.Files, "Undid the last action"),
        ["edit.redo"] = (ActivityKind.Files, "Redid the last action"),
        ["tag.apply"] = (ActivityKind.Files, "Tagged"),
        ["archive.compress"] = (ActivityKind.Files, "Compressed"),
        ["archive.browse"] = (ActivityKind.Files, "Browsed the archive"),
        ["archive.test"] = (ActivityKind.Files, "Tested the archive"),
        ["select.all"] = (ActivityKind.Files, "Selected all"),
        ["select.none"] = (ActivityKind.Files, "Selected none"),
        ["select.invert"] = (ActivityKind.Files, "Inverted the selection"),
        ["select.pattern"] = (ActivityKind.Files, "Selected by pattern"),
        ["tab.new"] = (ActivityKind.Tabs, "Opened a new tab"),
        ["tab.close"] = (ActivityKind.Tabs, "Closed a tab"),
        ["tab.reopen"] = (ActivityKind.Tabs, "Reopened a closed tab"),
        ["tab.duplicate"] = (ActivityKind.Tabs, "Duplicated a tab"),
        ["tab.tearoff"] = (ActivityKind.Tabs, "Moved a tab to its own window"),
        ["window.new"] = (ActivityKind.Tabs, "Opened a new window"),
        ["view.layout"] = (ActivityKind.View, "Changed the layout"),
        ["view.details"] = (ActivityKind.View, "Switched to Details"),
        ["view.list"] = (ActivityKind.View, "Switched to List"),
        ["view.thumbnails"] = (ActivityKind.View, "Switched to Thumbnails"),
        ["view.zoom"] = (ActivityKind.View, "Changed the thumbnail size"),
        ["view.columns"] = (ActivityKind.View, "Changed the columns"),
        ["view.hidden"] = (ActivityKind.View, "Toggled hidden items"),
        ["view.sort"] = (ActivityKind.View, "Sorted by"),
        ["sort.by"] = (ActivityKind.View, "Sorted by"),
        ["sort.direction"] = (ActivityKind.View, "Changed the sort direction"),
        ["sort.folders"] = (ActivityKind.View, "Changed where folders sort"),
        ["group.by"] = (ActivityKind.View, "Grouped by"),
        ["filter.set"] = (ActivityKind.View, "Filtered by"),
        ["pane.sidebar"] = (ActivityKind.View, "Toggled the sidebar"),
        ["pane.preview"] = (ActivityKind.View, "Toggled the preview pane"),
        ["hub.toggle"] = (ActivityKind.View, "Toggled the status hub"),
        ["drive.card"] = (ActivityKind.View, "Toggled the drive summary"),
        ["favorite.toggle"] = (ActivityKind.Settings, "Pinned or unpinned"),
        ["favorite.add"] = (ActivityKind.Settings, "Added to favorites"),
        ["favorite.rename"] = (ActivityKind.Settings, "Renamed a favorite"),
        ["favorite.style"] = (ActivityKind.Settings, "Changed a favorite's icon or colour"),
        ["profile.save"] = (ActivityKind.Settings, "Saved a profile"),
        ["profile.open"] = (ActivityKind.Settings, "Opened a profile"),
        ["profile.delete"] = (ActivityKind.Settings, "Deleted a profile"),
    };

    // Commands that say nothing the user did: the sidebar click is followed by the navigation it causes, and moving focus or
    // opening the hub is the window's chrome, not an action on files.
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal) { "sidebar.open", "address.focus", "hub.toggle", "filter.focus" };

    /// <summary>True for a command that is not worth a line in the log.</summary>
    public static bool IsNoise(string command) => Noise.Contains(command);

    /// <summary>The kind, the words and the place to jump to for a command that was run.</summary>
    public static (ActivityKind Kind, string Summary, string? Location) Describe(string command, string? target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var (kind, text) = Known.TryGetValue(command, out var known) ? known : (KindOfPrefix(command), command);
        var location = target is not null && LocationPath.TryParse(target, out var parsed) ? parsed.Value : null;
        var shown = target is { Length: > 0 } ? (location is not null ? LocationPath.Parse(location).Name : target) : null;
        var summary = shown is null ? text : $"{text} {shown}";
        return (kind, summary, location);
    }

    private static ActivityKind KindOfPrefix(string command) => command.Split('.')[0] switch
    {
        "nav" or "address" or "sidebar" => ActivityKind.Navigation,
        "item" or "edit" or "new" or "select" or "tag" or "archive" or "image" or "media" => ActivityKind.Files,
        "tab" or "window" => ActivityKind.Tabs,
        "view" or "sort" or "group" or "filter" or "pane" or "hub" or "drive" => ActivityKind.View,
        "favorite" or "profile" => ActivityKind.Settings,
        _ => ActivityKind.Other,
    };
}
