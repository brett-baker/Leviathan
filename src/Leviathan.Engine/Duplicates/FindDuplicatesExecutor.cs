using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Duplicates;

/// <summary>
/// Runs <see cref="JobKind.FindDuplicates"/> (spec §15.2) in the worker so a search over a drive or a share never touches the
/// window's thread and survives it closing. It reads files and changes nothing. Progress is bytes read and files listed; each
/// duplicate file is reported as an item whose result is its group key, so the window can rebuild the groups.
/// </summary>
public sealed class FindDuplicatesExecutor(IFileSystem fileSystem) : IJobExecutor
{
    private readonly DuplicateFinder _finder = new(fileSystem);

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.FindDuplicates;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var roots = new List<string>();
        foreach (var source in request.Sources)
        {
            var location = LocationPath.Parse(source);
            if (location.Kind is LocationKind.Drive or LocationKind.Unc)
            {
                roots.Add(location.ToWin32Path());
            }
            else
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Skipped, "Only folders on this PC and on Windows shares can be searched."));
            }
        }

        var options = new DuplicateOptions(request.DuplicateMinimumBytes, request.DuplicateExtensions, request.DuplicateIncludeHidden);
        var progress = new InlineProgress(p => context.Report(p.BytesRead, -1, p.Checked, p.Candidates > 0 ? p.Candidates : -1, p.Current));
        var result = await _finder.FindAsync(roots, options, progress, cancellationToken).ConfigureAwait(false);

        foreach (var group in result.Groups)
        {
            foreach (var path in group.Paths)
            {
                context.AddResult(new ItemOutcome(path, group.Key, ItemStatus.Done));
            }
        }

        foreach (var note in result.Unreadable)
        {
            context.AddNote("Could not read " + note);
        }

        var wasted = result.Groups.Sum(g => g.Wasted);
        context.AddNote($"{result.FilesScanned:N0} files compared: {result.Groups.Count:N0} groups of duplicates, {wasted:N0} bytes taken by the extra copies.");
        return JobOutcome.Completed;
    }

    /// <summary>Calls back at once on the reading thread; <see cref="Progress{T}"/> would post to a context.</summary>
    private sealed class InlineProgress(Action<DuplicateProgress> report) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value) => report(value);
    }
}
