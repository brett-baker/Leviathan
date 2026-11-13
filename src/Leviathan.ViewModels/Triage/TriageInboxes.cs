using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Sessions;

namespace Leviathan.ViewModels.Triage;

/// <summary>The order a session takes the next files of an inbox in (spec §10.1 "Sessions").</summary>
public enum TriageOrder
{
    /// <summary>Oldest modified first.</summary>
    Oldest,

    /// <summary>Newest modified first.</summary>
    Newest,

    /// <summary>Largest first, to reclaim the most space early.</summary>
    Largest,

    /// <summary>Shuffled, so a long inbox is sampled evenly.</summary>
    Random,
}

/// <summary>
/// A saved, named queue over a folder (spec §10.1 "Triage inboxes"). It remembers every video it has seen and every one that got a Keep or
/// Discard, so "untriaged" is a set difference and never a tag read per file, even over 30,000 files. Paths compare ignoring case.
/// </summary>
public sealed class TriageInbox
{
    /// <summary>Stable id; the name can change.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The name shown in the list, e.g. "NAS inbox".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder the inbox watches.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Include sub-folders.</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>The order sessions take files in.</summary>
    public TriageOrder Order { get; set; } = TriageOrder.Oldest;

    /// <summary>Files per session (default 1,000).</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>True once the folder has been read at least once; arrivals are only "New" after that.</summary>
    public bool Scanned { get; set; }

    /// <summary>Every video path seen so far.</summary>
    public HashSet<string> Known { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths that got a Keep or a Discard.</summary>
    public HashSet<string> Decided { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths that arrived after the first scan and have not been looked at.</summary>
    public HashSet<string> New { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the folder was last read.</summary>
    public DateTimeOffset? LastScan { get; set; }
}

/// <summary>The saved inboxes (<c>inboxes.json</c>).</summary>
public sealed class InboxesFile
{
    /// <summary>The inboxes.</summary>
    public List<TriageInbox> Inboxes { get; set; } = [];
}

/// <summary>What a folder read found.</summary>
/// <param name="All">Every video in the inbox now.</param>
/// <param name="Arrived">How many were not there at the last read.</param>
public sealed record InboxScan(IReadOnlyList<FileEntry> All, int Arrived);

/// <summary>One session's files and the numbers the window shows.</summary>
/// <param name="Inbox">The inbox.</param>
/// <param name="Queue">The files of this session, in order.</param>
/// <param name="Total">Videos in the inbox.</param>
/// <param name="Triaged">Videos already kept or discarded.</param>
/// <param name="Arrived">New arrivals since the last read.</param>
public sealed record InboxSession(TriageInbox Inbox, IReadOnlyList<FileEntry> Queue, int Total, int Triaged, int Arrived)
{
    /// <summary>"612 / 1,000 this session · 4,212 of 31,880 triaged" (spec §10.1).</summary>
    public static string Progress(int done, int batch, int triaged, int total) =>
        $"{done:N0} / {batch:N0} this session · {triaged:N0} of {total:N0} triaged";
}

/// <summary>
/// The saved inboxes: create, rename, delete, read the folder for arrivals, pick the next session's files and record its decisions
/// (spec §10.1). State is one JSON file written atomically; a folder read streams from the provider and can be cancelled.
/// </summary>
public sealed class InboxCatalog : IDisposable
{
    private readonly JsonFileStore<InboxesFile> _store;
    private readonly TriageJournal? _journal;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InboxesFile? _file;

    /// <summary>
    /// Creates a catalog over <paramref name="store"/>. With a <paramref name="journal"/>, decisions a session made but never got to save
    /// (the app crashed) are folded into the inboxes the first time they are read.
    /// </summary>
    public InboxCatalog(JsonFileStore<InboxesFile> store, TimeProvider? time = null, TriageJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _time = time ?? TimeProvider.System;
        _journal = journal;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>The saved inboxes, in the order they were made.</summary>
    public async Task<IReadOnlyList<TriageInbox>> ListAsync(CancellationToken cancellationToken)
    {
        var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return [.. file.Inboxes];
    }

    /// <summary>Saves a new inbox over <paramref name="folder"/>; it is read on first use.</summary>
    public async Task<TriageInbox> AddAsync(string name, string folder, bool recursive, TriageOrder order, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var inbox = new TriageInbox { Name = name.Trim(), Folder = folder.Trim(), Recursive = recursive, Order = order, BatchSize = Math.Clamp(batchSize, 1, TriageQueue.MaxItems) };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            file.Inboxes.Add(inbox);
            await _store.SaveAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return inbox;
    }

    /// <summary>Removes the inbox; the files are not touched.</summary>
    public Task RemoveAsync(TriageInbox inbox, CancellationToken cancellationToken) =>
        MutateAsync(file => file.Inboxes.RemoveAll(i => i.Id == inbox.Id), cancellationToken);

    /// <summary>Changes the order and batch size of an inbox.</summary>
    public Task SetSessionOptionsAsync(TriageInbox inbox, TriageOrder order, int batchSize, CancellationToken cancellationToken) =>
        MutateAsync(_ =>
        {
            inbox.Order = order;
            inbox.BatchSize = Math.Clamp(batchSize, 1, TriageQueue.MaxItems);
        }, cancellationToken);

    /// <summary>
    /// Reads the folder and appends what is new: files not seen before become "New" (the first read is the baseline, so nothing is New
    /// then). Called when the inbox is opened and by the low-priority check.
    /// </summary>
    public async Task<InboxScan> ScanAsync(TriageInbox inbox, ILocationProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(provider);
        var all = await TriageQueue.FromFolderAsync(
            provider, LocationPath.Parse(inbox.Folder), new TriageOptions { Recursive = inbox.Recursive }, null, TriageQueue.ByName, null, cancellationToken).ConfigureAwait(false);
        var arrived = 0;
        await MutateAsync(_ =>
        {
            foreach (var entry in all)
            {
                if (inbox.Known.Add(entry.Location.Value) && inbox.Scanned)
                {
                    inbox.New.Add(entry.Location.Value);
                    arrived++;
                }
            }

            inbox.Scanned = true;
            inbox.LastScan = _time.GetUtcNow();
        }, cancellationToken).ConfigureAwait(false);
        return new InboxScan(all, arrived);
    }

    /// <summary>The next session: the first <see cref="TriageInbox.BatchSize"/> files with no decision, in the inbox's order.</summary>
    /// <param name="inbox">The inbox.</param>
    /// <param name="scan">The files from <see cref="ScanAsync"/>.</param>
    /// <param name="random">Used for <see cref="TriageOrder.Random"/>; fixed in tests.</param>
    public static InboxSession NextSession(TriageInbox inbox, InboxScan scan, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(scan);
        var pending = scan.All.Where(f => !inbox.Decided.Contains(f.Location.Value));
        IEnumerable<FileEntry> ordered = inbox.Order switch
        {
            TriageOrder.Newest => pending.OrderByDescending(f => f.Modified).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            TriageOrder.Largest => pending.OrderByDescending(f => f.Size).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            TriageOrder.Random => Shuffle(pending, random ?? Random.Shared),
            _ => pending.OrderBy(f => f.Modified).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
        var queue = ordered.Take(Math.Max(1, inbox.BatchSize)).ToList();
        var triaged = scan.All.Count(f => inbox.Decided.Contains(f.Location.Value));
        return new InboxSession(inbox, queue, scan.All.Count, triaged, scan.Arrived);
    }

    /// <summary>Remembers what a session decided: kept and discarded files are triaged for good; every file it reached stops being New.</summary>
    public async Task RecordAsync(TriageInbox inbox, TriageSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(session);
        await MutateAsync(_ =>
        {
            foreach (var item in session.Items)
            {
                var path = item.Original.Location.Value;
                if (item.Decision is TriageDecision.Kept or TriageDecision.Discarded)
                {
                    inbox.Decided.Add(path);
                }

                if (item.Decision != TriageDecision.None)
                {
                    inbox.New.Remove(path);
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        // Saved in full now, so the journal no longer needs this inbox's decisions (kept files still waiting for a move stay in it).
        if (_journal is not null)
        {
            try
            {
                await Task.Run(() => _journal.Compact(r => r.Scope == inbox.Id && !r.IsPendingMove), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left as it is: the next start folds the same decisions in again, which changes nothing.
            }
        }
    }

    /// <summary>How many decisions were recovered from the journal when the inboxes were first read (non-zero after a crash).</summary>
    public int RecoveredCount { get; private set; }

    // Folds what the journal holds into the inboxes: a kept or discarded file is decided, an undone or skipped one is not. What was
    // folded in is then dropped from the journal, except kept files still waiting for their move, which the app offers again.
    private void Recover(InboxesFile file)
    {
        if (_journal is null)
        {
            return;
        }

        try
        {
            var applied = 0;
            foreach (var record in _journal.Replay())
            {
                if (file.Inboxes.FirstOrDefault(i => i.Id == record.Scope) is not { } inbox)
                {
                    continue;
                }

                if (record.Decision is TriageDecision.Kept or TriageDecision.Discarded)
                {
                    applied += inbox.Decided.Add(record.Path) ? 1 : 0;
                }
                else if (record.Decision == TriageDecision.None)
                {
                    applied += inbox.Decided.Remove(record.Path) ? 1 : 0;
                }

                if (record.Decision != TriageDecision.None)
                {
                    inbox.New.Remove(record.Path);
                }
            }

            RecoveredCount = applied;
            _journal.Compact(r => !r.IsPendingMove);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The journal is a safety net; if it cannot be read the inboxes are used as they were saved.
        }
    }

    /// <summary>How many files in this inbox are decided, for the progress line.</summary>
    public static int TriagedCount(TriageInbox inbox, TriageSession session) =>
        inbox.Decided.Count + session.Items.Count(i => i.Decision is TriageDecision.Kept or TriageDecision.Discarded && !inbox.Decided.Contains(i.Original.Location.Value));

    private static List<FileEntry> Shuffle(IEnumerable<FileEntry> files, Random random)
    {
        var list = files.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private async Task<InboxesFile> LoadAsync(CancellationToken cancellationToken)
    {
        if (_file is not null)
        {
            return _file;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_file is not null)
            {
                return _file;
            }

            var file = await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new InboxesFile();
            await Task.Run(() => Recover(file), cancellationToken).ConfigureAwait(false);
            if (_journal is not null && file.Inboxes.Count > 0 && RecoveredCount > 0)
            {
                await _store.SaveAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return _file = file;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MutateAsync(Action<InboxesFile> change, CancellationToken cancellationToken)
    {
        var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            change(file);
            await _store.SaveAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
