using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Contracts;

namespace Leviathan.ViewModels.Hub;

/// <summary>What the Operations tab can do to the selected jobs (spec §12.6).</summary>
public enum OperationAction
{
    /// <summary>Stop a job without losing its place; it can resume.</summary>
    Pause,

    /// <summary>Let a paused job go on.</summary>
    Resume,

    /// <summary>End a job; a copy keeps what it finished, a delete stops before its next item.</summary>
    Cancel,

    /// <summary>Take a job that is not running off the list.</summary>
    Remove,

    /// <summary>Start a queued or paused job now, above the limits.</summary>
    RunNow,

    /// <summary>One place earlier in the queue.</summary>
    MoveUp,

    /// <summary>One place later in the queue.</summary>
    MoveDown,
}

/// <summary>The worker's job controls the Operations tab uses; the app implements them over the worker connection, tests with a fake.</summary>
public interface IJobControl
{
    /// <summary>Pauses a queued, waiting or running job.</summary>
    Task PauseAsync(Guid jobId);

    /// <summary>Resumes a paused job.</summary>
    Task ResumeAsync(Guid jobId);

    /// <summary>Cancels a job.</summary>
    Task CancelAsync(Guid jobId);

    /// <summary>Removes a job that is not running.</summary>
    Task RemoveAsync(Guid jobId);

    /// <summary>Starts a queued or paused job now.</summary>
    Task RunNowAsync(Guid jobId);

    /// <summary>Moves a queued job to a place in the queue (0 = next).</summary>
    Task MoveAsync(Guid jobId, int queuePosition);

    /// <summary>Changes how soon a queued job runs.</summary>
    Task SetPriorityAsync(Guid jobId, JobPriority priority);

    /// <summary>Pauses every unfinished job.</summary>
    Task PauseAllAsync();

    /// <summary>Resumes every paused job.</summary>
    Task ResumeAllAsync();
}

/// <summary>
/// Applies a row action to the selected jobs of the Operations tab (spec §12.6): only the jobs the action fits are touched, so
/// "Pause" on a mixed selection pauses the running ones and leaves the finished ones alone. Moving keeps the selected jobs in
/// their relative order.
/// </summary>
public sealed class OperationActions(IJobControl control)
{
    /// <summary>True when <paramref name="action"/> fits at least one of <paramref name="selected"/>: the button is enabled.</summary>
    public static bool CanDo(OperationAction action, IEnumerable<OperationEntry> selected) => selected.Any(e => Fits(action, e));

    /// <summary>True when <paramref name="action"/> fits <paramref name="entry"/>.</summary>
    public static bool Fits(OperationAction action, OperationEntry entry) => action switch
    {
        OperationAction.Pause => entry.CanPause,
        OperationAction.Resume => entry.CanResume,
        OperationAction.Cancel => entry.CanCancel,
        OperationAction.Remove => entry.CanRemove,
        OperationAction.RunNow => entry.CanRunNow,
        OperationAction.MoveUp => entry.CanMoveUp,
        OperationAction.MoveDown => entry.CanMoveDown,
        _ => false,
    };

    /// <summary>Does <paramref name="action"/> to the jobs among <paramref name="selected"/> it fits. Returns how many were touched.</summary>
    public async Task<int> RunAsync(OperationAction action, IEnumerable<OperationEntry> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var targets = selected.Where(e => Fits(action, e)).ToList();

        // Earlier first when going up, last first when going down, so the jobs do not step over each other.
        targets = action switch
        {
            OperationAction.MoveUp => [.. targets.OrderBy(e => e.QueuePosition)],
            OperationAction.MoveDown => [.. targets.OrderByDescending(e => e.QueuePosition)],
            _ => targets,
        };
        foreach (var entry in targets)
        {
            await (action switch
            {
                OperationAction.Pause => control.PauseAsync(entry.Id),
                OperationAction.Resume => control.ResumeAsync(entry.Id),
                OperationAction.Cancel => control.CancelAsync(entry.Id),
                OperationAction.Remove => control.RemoveAsync(entry.Id),
                OperationAction.RunNow => control.RunNowAsync(entry.Id),
                OperationAction.MoveUp => control.MoveAsync(entry.Id, entry.QueuePosition - 1),
                _ => control.MoveAsync(entry.Id, entry.QueuePosition + 1),
            }).ConfigureAwait(false);
        }

        return targets.Count;
    }

    /// <summary>Gives the unfinished jobs among <paramref name="selected"/> a priority.</summary>
    public async Task<int> SetPriorityAsync(JobPriority priority, IEnumerable<OperationEntry> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var targets = selected.Where(e => e.IsActive && e.Priority != priority).ToList();
        foreach (var entry in targets)
        {
            await control.SetPriorityAsync(entry.Id, priority).ConfigureAwait(false);
        }

        return targets.Count;
    }

    /// <summary>Pauses every unfinished job (the toolbar's "Pause all").</summary>
    public Task PauseAllAsync() => control.PauseAllAsync();

    /// <summary>Resumes every paused job (the toolbar's "Resume all").</summary>
    public Task ResumeAllAsync() => control.ResumeAllAsync();
}

/// <summary>A line of the Operations list: a group heading or a job.</summary>
public abstract class OperationListItem : ObservableObject
{
    /// <summary>Identifies the line across refreshes, so a refresh keeps the rows (and the scroll position and selection) it can.</summary>
    public abstract string Key { get; }
}

/// <summary>The heading of a group.</summary>
public sealed class OperationHeader(OperationGroupKind kind, string title) : OperationListItem
{
    private string _title = title;

    /// <summary>Which group it heads.</summary>
    public OperationGroupKind Kind { get; } = kind;

    /// <summary>The heading, e.g. "Running (2)".</summary>
    public string Title
    {
        get => _title;
        internal set => SetProperty(ref _title, value);
    }

    /// <inheritdoc />
    public override string Key => "group:" + Kind;
}

/// <summary>A job's line; <see cref="Entry"/> is replaced as progress arrives and the line redraws itself.</summary>
public sealed class OperationRow(OperationEntry entry) : OperationListItem
{
    private OperationEntry _entry = entry;

    /// <summary>The job as last reported.</summary>
    public OperationEntry Entry
    {
        get => _entry;
        internal set => SetProperty(ref _entry, value);
    }

    /// <inheritdoc />
    public override string Key => _entry.Id.ToString("N");
}

/// <summary>Keeps the lines of the Operations list in step with the model without replacing them all (spec §3.2).</summary>
public static class OperationLines
{
    /// <summary>
    /// Brings <paramref name="lines"/> in line with <paramref name="groups"/>: lines that are still wanted are updated in place, new
    /// ones are inserted, the rest are removed. A list that is replaced on every progress report scrolls back to the top and loses
    /// the selection, which is why this does not.
    /// </summary>
    public static void Sync(ObservableCollection<OperationListItem> lines, IReadOnlyList<OperationGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(groups);
        var index = 0;
        foreach (var group in groups)
        {
            Place(lines, ref index, new OperationHeader(group.Kind, group.Title));
            foreach (var entry in group.Entries)
            {
                Place(lines, ref index, new OperationRow(entry));
            }
        }

        while (lines.Count > index)
        {
            lines.RemoveAt(lines.Count - 1);
        }
    }

    private static void Place(ObservableCollection<OperationListItem> lines, ref int index, OperationListItem wanted)
    {
        if (index < lines.Count && lines[index].Key == wanted.Key)
        {
            Update(lines[index], wanted);
            index++;
            return;
        }

        // The wanted line may be further down (lines before it ended): drop what is in the way. Otherwise it is new.
        var found = -1;
        for (var i = index + 1; i < lines.Count; i++)
        {
            if (lines[i].Key == wanted.Key)
            {
                found = i;
                break;
            }
        }

        if (found >= 0)
        {
            for (var i = found - 1; i >= index; i--)
            {
                lines.RemoveAt(i);
            }

            Update(lines[index], wanted);
        }
        else
        {
            lines.Insert(index, wanted);
        }

        index++;
    }

    private static void Update(OperationListItem current, OperationListItem wanted)
    {
        switch (current, wanted)
        {
            case (OperationRow row, OperationRow next) when row.Entry != next.Entry:
                row.Entry = next.Entry;
                break;
            case (OperationHeader header, OperationHeader next) when header.Title != next.Title:
                header.Title = next.Title;
                break;
        }
    }
}
