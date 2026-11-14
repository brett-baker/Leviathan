using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leviathan.Core.Compare;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Sync;

/// <summary>
/// A pair of folders and the way to bring them into line, saved to be run again with one click (spec §15.5). It remembers the modes and
/// settings, never which items were skipped: every run compares afresh and shows its dry run first.
/// </summary>
public sealed record SyncPair
{
    /// <summary>Identifies the pair.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The name shown in Favorites.</summary>
    public string Name { get; init; } = "Sync pair";

    /// <summary>The left folder (OS path).</summary>
    public string Left { get; init; } = string.Empty;

    /// <summary>The right folder (OS path).</summary>
    public string Right { get; init; } = string.Empty;

    /// <summary>How the folders are brought into line.</summary>
    public SyncMode Mode { get; init; } = SyncMode.TwoWay;

    /// <summary>How files are told apart.</summary>
    public CompareBy By { get; init; } = CompareBy.NameSizeDate;

    /// <summary>Times this many seconds apart count as the same.</summary>
    public int ToleranceSeconds { get; init; } = 2;

    /// <summary>When the pair was saved.</summary>
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Check for changes every this many minutes while Leviathan runs; 0 for never. A check compares and records what is waiting; it does not change files unless <see cref="AutoRun"/> says so and the mode allows it.</summary>
    public int CheckEveryMinutes { get; init; }

    /// <summary>
    /// Run the sync on its own after a check. Only honoured for the update modes, which never delete and never overwrite a newer item
    /// (see <see cref="SyncSchedule.MayRunUnattended"/>): a mirror deletes, a two-way sync can hit conflicts, and neither runs without
    /// the person seeing the preview.
    /// </summary>
    public bool AutoRun { get; init; }

    /// <summary>When the pair was last checked, by a person or by the schedule.</summary>
    public DateTimeOffset? LastChecked { get; init; }

    /// <summary>What the last check found, in words ("3 changes waiting", "up to date").</summary>
    public string? LastResult { get; init; }
}

/// <summary>The special folder that stands for a saved sync pair in the sidebar: clicking it opens the pair instead of listing a folder.</summary>
public static class SyncPairLocations
{
    /// <summary>The root of all sync pairs.</summary>
    public const string Root = "shell:syncpair";

    /// <summary>The location that stands for <paramref name="id"/>.</summary>
    public static LocationPath For(Guid id) => LocationPath.Parse(Root + "\\" + id.ToString("N"));

    /// <summary>The pair a location stands for, or null when it is not one.</summary>
    public static Guid? IdOf(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Kind == LocationKind.Shell && string.Equals(location.Root, Root, StringComparison.OrdinalIgnoreCase) && location.Segments.Count == 1 && Guid.TryParseExact(location.Segments[0], "N", out var id) ? id : null;
    }
}

/// <summary>When a pair is due for a check, and what may run without a person looking (spec §15.5). Pure, so it is tested with a clock in hand.</summary>
public static class SyncSchedule
{
    /// <summary>The choices offered for "check automatically", in minutes (0 = never).</summary>
    public static IReadOnlyList<int> Intervals { get; } = [0, 15, 60, 360, 1440];

    /// <summary>The words for an interval: "Never", "Every hour", "Every day".</summary>
    public static string Describe(int minutes) => minutes switch
    {
        <= 0 => "Never",
        < 60 => $"Every {minutes} minutes",
        60 => "Every hour",
        1440 => "Every day",
        _ when minutes % 60 == 0 => $"Every {minutes / 60} hours",
        _ => $"Every {minutes} minutes",
    };

    /// <summary>The pairs whose time has come at <paramref name="now"/>: those with an interval whose last check (or creation) is at least that long ago.</summary>
    public static IReadOnlyList<SyncPair> Due(IEnumerable<SyncPair> pairs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return [.. pairs.Where(p => p.CheckEveryMinutes > 0 && now - (p.LastChecked ?? p.Created) >= TimeSpan.FromMinutes(p.CheckEveryMinutes))];
    }

    /// <summary>
    /// True when a sync of this pair may run with nobody looking: the person asked for it and the mode is an update, which only adds and
    /// replaces older files with newer ones. A mirror deletes and a two-way sync can meet conflicts, so those only ever check and report.
    /// </summary>
    public static bool MayRunUnattended(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.AutoRun && pair.Mode is SyncMode.UpdateLeftToRight or SyncMode.UpdateRightToLeft;
    }

    /// <summary>What a check found, for the sidebar and the window: "up to date", "12 changes waiting", "3 conflicts".</summary>
    public static string Summarize(SyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new List<string>();
        if (plan.Actions.Count > 0)
        {
            parts.Add(plan.Actions.Count == 1 ? "1 change waiting" : $"{plan.Actions.Count:N0} changes waiting");
        }

        if (plan.Conflicts.Count > 0)
        {
            parts.Add(plan.Conflicts.Count == 1 ? "1 conflict" : $"{plan.Conflicts.Count:N0} conflicts");
        }

        return parts.Count == 0 ? "up to date" : string.Join(", ", parts);
    }
}

/// <summary>The saved sync pairs, in a JSON file written atomically (temp file then rename), so a crash never leaves half a list.</summary>
public sealed class SyncPairStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly List<SyncPair> _pairs;

    /// <summary>Loads the pairs from <paramref name="path"/>; a missing or unreadable file is an empty list.</summary>
    public SyncPairStore(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _fileSystem = fileSystem;
        _path = path;
        _pairs = Load();
    }

    /// <summary>Raised after the list changed (on whichever thread changed it).</summary>
    public event EventHandler? Changed;

    /// <summary>The saved pairs in the order they were saved.</summary>
    public IReadOnlyList<SyncPair> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _pairs];
            }
        }
    }

    /// <summary>The pair with <paramref name="id"/>, or null.</summary>
    public SyncPair? Find(Guid id) => Items.FirstOrDefault(p => p.Id == id);

    /// <summary>Adds a pair, or replaces the one with the same id.</summary>
    public void Save(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        Change(list =>
        {
            var at = list.FindIndex(p => p.Id == pair.Id);
            if (at >= 0)
            {
                list[at] = pair;
            }
            else
            {
                list.Add(pair);
            }
        });
    }

    /// <summary>Forgets a pair.</summary>
    public void Remove(Guid id) => Change(list => list.RemoveAll(p => p.Id == id));

    /// <summary>Notes that the pair was checked at <paramref name="when"/> and what was found.</summary>
    public void RecordCheck(Guid id, DateTimeOffset when, string result) =>
        Change(list =>
        {
            var at = list.FindIndex(p => p.Id == id);
            if (at >= 0)
            {
                list[at] = list[at] with { LastChecked = when, LastResult = result };
            }
        });

    private void Change(Action<List<SyncPair>> edit)
    {
        lock (_lock)
        {
            edit(_pairs);
            try
            {
                var folder = _fileSystem.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(folder))
                {
                    _fileSystem.Directory.CreateDirectory(folder);
                }

                var temp = _path + ".tmp";
                _fileSystem.File.WriteAllText(temp, JsonSerializer.Serialize(_pairs, Json));
                _fileSystem.File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The change holds for this run; it is just not remembered.
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<SyncPair> Load()
    {
        try
        {
            return _fileSystem.File.Exists(_path) ? JsonSerializer.Deserialize<List<SyncPair>>(_fileSystem.File.ReadAllText(_path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
