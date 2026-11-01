using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.FolderSizes;

/// <summary>Runs <see cref="JobKind.ComputeFolderSize"/> jobs in the background lane.</summary>
public sealed class ComputeFolderSizeExecutor : IJobExecutor
{
    private readonly FolderSizeService _service;

    /// <summary>Creates the executor.</summary>
    public ComputeFolderSizeExecutor(FolderSizeService service) => _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.ComputeFolderSize;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sources = context.Job.Request.Sources;
        long bytesSoFar = 0;
        var filesSoFar = 0;
        var errors = new List<string>();

        for (var i = 0; i < sources.Count; i++)
        {
            var location = LocationPath.Parse(sources[i]);
            var baseBytes = bytesSoFar;
            var baseFiles = filesSoFar;
            var progress = new SyncProgress<FolderSize>(p =>
                context.Report(baseBytes + p.TotalBytes, -1, baseFiles + p.TotalFiles, -1, location.Value));

            var result = await _service.ComputeAsync(location, progress, cancellationToken).ConfigureAwait(false);
            bytesSoFar += result.TotalBytes;
            filesSoFar += result.TotalFiles;
            context.Report(bytesSoFar, i == sources.Count - 1 ? bytesSoFar : -1, filesSoFar, i == sources.Count - 1 ? filesSoFar : -1, location.Value);
            if (result.State == FolderSizeState.Error)
            {
                errors.Add($"Some folders under {location} could not be read.");
            }
        }

        return errors.Count == 0 ? JobOutcome.Completed : new JobOutcome(JobState.Completed, string.Join(" ", errors));
    }

    /// <summary>Invokes the callback synchronously (unlike <see cref="Progress{T}"/>, which posts to a sync context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
