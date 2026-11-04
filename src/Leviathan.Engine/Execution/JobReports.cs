using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leviathan.Contracts;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Engine.Execution;

/// <summary>Builds the report of a finished job from what its executor recorded (spec §12.3).</summary>
public static class JobReports
{
    /// <summary>The report for <paramref name="job"/>, which must be finished.</summary>
    public static JobReport Build(ScheduledJob job, JobContext? context, DateTimeOffset finishedAt)
    {
        ArgumentNullException.ThrowIfNull(job);
        IReadOnlyList<ItemOutcome> items = context?.Results ?? [];
        return new JobReport(
            job.Id, job.Title, job.Request.Kind, job.State, job.FinishedAt ?? finishedAt, context?.BytesDone ?? 0,
            items.Count(i => i.Status == ItemStatus.Done), items.Count(i => i.Status == ItemStatus.Skipped), items.Count(i => i.Status == ItemStatus.Failed),
            items, context?.Notes ?? [], job.Error);
    }
}

/// <summary>Keeps job reports for a while after their jobs finish.</summary>
public interface IJobReportStore
{
    /// <summary>Stores (or replaces) a report.</summary>
    void Save(JobReport report);

    /// <summary>The report of a job, or null.</summary>
    JobReport? TryLoad(Guid jobId);

    /// <summary>Deletes reports older than <paramref name="retention"/>.</summary>
    void Prune(TimeSpan retention, DateTimeOffset now);
}

/// <summary>Reports in memory, for tests.</summary>
public sealed class InMemoryJobReportStore : IJobReportStore
{
    private readonly Dictionary<Guid, JobReport> _reports = [];

    /// <inheritdoc />
    public void Save(JobReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_reports)
        {
            _reports[report.JobId] = report;
        }
    }

    /// <inheritdoc />
    public JobReport? TryLoad(Guid jobId)
    {
        lock (_reports)
        {
            return _reports.GetValueOrDefault(jobId);
        }
    }

    /// <inheritdoc />
    public void Prune(TimeSpan retention, DateTimeOffset now)
    {
        lock (_reports)
        {
            foreach (var id in _reports.Where(r => now - r.Value.FinishedAt > retention).Select(r => r.Key).ToList())
            {
                _reports.Remove(id);
            }
        }
    }
}

/// <summary>One JSON file per report, written atomically; pruned by age (7 days).</summary>
public sealed class FileJobReportStore(IFileSystem fileSystem, string folder) : IJobReportStore
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    /// <inheritdoc />
    public void Save(JobReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            fileSystem.Directory.CreateDirectory(folder);
            var path = PathFor(report.JobId);
            fileSystem.File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, Options));
            fileSystem.File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A report is a convenience; losing one must never fail or hang the job it describes.
        }
    }

    /// <inheritdoc />
    public JobReport? TryLoad(Guid jobId)
    {
        try
        {
            var path = PathFor(jobId);
            return fileSystem.File.Exists(path) ? JsonSerializer.Deserialize<JobReport>(fileSystem.File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Prune(TimeSpan retention, DateTimeOffset now)
    {
        try
        {
            if (!fileSystem.Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in fileSystem.Directory.EnumerateFiles(folder, "*.report.json"))
            {
                if (now - new DateTimeOffset(fileSystem.File.GetLastWriteTimeUtc(file), TimeSpan.Zero) > retention)
                {
                    fileSystem.File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old reports linger until the next pass.
        }
    }

    private string PathFor(Guid jobId) => fileSystem.Path.Combine(folder, jobId.ToString("N") + ".report.json");
}
