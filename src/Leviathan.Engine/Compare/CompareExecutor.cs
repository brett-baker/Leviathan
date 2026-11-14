using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Compare;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Compare;

/// <summary>
/// Runs <see cref="JobKind.Compare"/> (spec §15.5) in the worker, in the background lane: listing two trees of 100,000 items on a share
/// and reading the files of equal size to tell them apart never touches the window's thread and survives it closing. It reads and changes
/// nothing; the preview and the sync are built from its result.
/// </summary>
public sealed class CompareExecutor(IFileSystem fileSystem) : IJobExecutor
{
    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.Compare;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var left = LocationPath.Parse(request.Sources[0]);
        var right = LocationPath.Parse(request.Destination!);
        if (left.Kind is not (LocationKind.Drive or LocationKind.Unc) || right.Kind is not (LocationKind.Drive or LocationKind.Unc))
        {
            return new JobOutcome(JobState.Failed, "Only folders on this PC and on Windows shares can be compared.");
        }

        var leftPath = left.ToWin32Path();
        var rightPath = right.ToWin32Path();
        if (!fileSystem.Directory.Exists(leftPath) || !fileSystem.Directory.Exists(rightPath))
        {
            return new JobOutcome(JobState.Failed, fileSystem.Directory.Exists(leftPath) ? $"{right.Name} is not there." : $"{left.Name} is not there.");
        }

        var options = new CompareOptions
        {
            By = request.CompareByChecksum ? CompareBy.Checksum : CompareBy.NameSizeDate,
            TimeTolerance = TimeSpan.FromSeconds(Math.Max(0, request.CompareToleranceSeconds)),
            IgnoreDaylightSaving = request.CompareIgnoreDaylightSaving,
        };
        var progress = new Inline(p => context.Report(p.Hashed, p.ToHash, p.Scanned, -1, p.Current));
        var result = await new FolderComparer(fileSystem).CompareAsync(leftPath, rightPath, options, progress, cancellationToken).ConfigureAwait(false);

        foreach (var entry in result.Entries)
        {
            context.AddResult(new ItemOutcome(entry.Path, CompareCodec.Encode(entry), ItemStatus.Done));
        }

        foreach (var problem in result.Problems)
        {
            context.AddNote("Could not read " + problem);
        }

        context.AddNote($"{result.Entries.Count:N0} items compared: {result.Count(CompareStatus.Identical):N0} identical, {result.Count(CompareStatus.LeftOnly):N0} only on the left, {result.Count(CompareStatus.RightOnly):N0} only on the right, {result.Count(CompareStatus.NewerLeft) + result.Count(CompareStatus.NewerRight):N0} newer on one side, {result.Count(CompareStatus.Different) + result.Count(CompareStatus.TypeMismatch):N0} different.");
        return JobOutcome.Completed;
    }

    private sealed class Inline(Action<CompareProgress> report) : IProgress<CompareProgress>
    {
        public void Report(CompareProgress value) => report(value);
    }
}
