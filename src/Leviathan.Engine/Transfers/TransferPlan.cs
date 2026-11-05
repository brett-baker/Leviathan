using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leviathan.Contracts;

namespace Leviathan.Engine.Transfers;

/// <summary>How the bytes of one file get from the source to the destination (spec §12.1 step 2).</summary>
public enum TransferPath
{
    /// <summary>Same volume move: a rename, instant, nothing is copied.</summary>
    Rename,

    /// <summary>Both ends on the same SMB server: <c>CopyFile2</c> lets the NAS copy at disk speed with no data crossing the network.</summary>
    ServerSideCopy,

    /// <summary>Leviathan's chunked copier: reads and writes through this machine.</summary>
    ChunkedCopy,
}

/// <summary>One thing the executor does, in order.</summary>
public enum PlanOp
{
    /// <summary>Rename a whole file or folder into place (a same-volume move without a name clash).</summary>
    RenameItem,

    /// <summary>Make a folder at the destination.</summary>
    CreateFolder,

    /// <summary>Copy or move one file, by the step's <see cref="PlanStep.Path"/>.</summary>
    CopyFile,

    /// <summary>After a move, remove the source folder when it has been emptied.</summary>
    RemoveFolder,
}

/// <summary>One step of a plan.</summary>
/// <param name="Op">What to do.</param>
/// <param name="Source">The source path (a folder for <see cref="PlanOp.RemoveFolder"/>).</param>
/// <param name="Target">The destination path, already resolved for name clashes.</param>
/// <param name="Size">Bytes this step moves, 0 for folders.</param>
/// <param name="Path">How the bytes travel; only meaningful for file steps.</param>
/// <param name="Clash">True while the target is an existing file the job has not yet decided about (spec §12.4); the executor resolves every clash before it copies anything.</param>
public sealed record PlanStep(PlanOp Op, string Source, string Target, long Size, TransferPath Path, bool Clash = false);

/// <summary>What will happen to one top-level source.</summary>
/// <param name="Source">The source as the user gave it.</param>
/// <param name="Target">Where it ends up, or null when nothing is made.</param>
/// <param name="Status"><see cref="ItemStatus.Done"/> when there are steps to run; Skipped or Failed when the planner already decided (nothing to run).</param>
/// <param name="Note">Why it was skipped or failed.</param>
/// <param name="Size">Bytes this item moves.</param>
/// <param name="Steps">The steps, in execution order.</param>
public sealed record PlanItem(string Source, string? Target, ItemStatus Status, string? Note, long Size, IReadOnlyList<PlanStep> Steps);

/// <summary>
/// The full item list of a copy or move, decided before anything is copied (spec §12.1 step 1): every file with its resolved target and
/// the fastest path for it, so the totals, the free-space check and the progress bar are exact from the first byte, and a restart
/// has a record of what the job was going to do.
/// </summary>
/// <param name="JobId">The job.</param>
/// <param name="Move">True for a move.</param>
/// <param name="Conflict">The conflict policy the plan was made with.</param>
/// <param name="Destination">The destination folder.</param>
/// <param name="Items">One per source, in order.</param>
public sealed record TransferPlan(Guid JobId, bool Move, ConflictPolicy Conflict, string Destination, IReadOnlyList<PlanItem> Items)
{
    /// <summary>Total bytes the job moves, for the progress bar.</summary>
    [JsonIgnore]
    public long BytesTotal => Items.Sum(i => i.Size);

    /// <summary>Bytes that must be written at the destination: everything except renames. Zero for a same-volume move.</summary>
    [JsonIgnore]
    public long BytesToWrite => Items.SelectMany(i => i.Steps).Where(s => s.Op == PlanOp.CopyFile && s.Path != TransferPath.Rename).Sum(s => s.Size);

    /// <summary>The file steps that would overwrite an existing file and wait for a decision (spec §12.4).</summary>
    [JsonIgnore]
    public IEnumerable<PlanStep> Clashes => Items.SelectMany(i => i.Steps).Where(s => s.Clash);

    /// <summary>Number of files the job touches.</summary>
    [JsonIgnore]
    public int FileCount => Items.SelectMany(i => i.Steps).Count(s => s.Op is PlanOp.CopyFile or PlanOp.RenameItem);
}

/// <summary>Where plans are kept while their jobs are unfinished.</summary>
public interface ITransferPlanStore
{
    /// <summary>Writes the plan, replacing any earlier one for the job.</summary>
    void Save(TransferPlan plan);

    /// <summary>The saved plan, or null when there is none or it cannot be read.</summary>
    TransferPlan? TryLoad(Guid jobId);

    /// <summary>Forgets the plan of a finished job.</summary>
    void Delete(Guid jobId);
}

/// <summary>Keeps plans in memory, for tests and for hosts without a data folder.</summary>
public sealed class InMemoryTransferPlanStore : ITransferPlanStore
{
    private readonly Dictionary<Guid, TransferPlan> _plans = [];

    /// <inheritdoc />
    public void Save(TransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_plans)
        {
            _plans[plan.JobId] = plan;
        }
    }

    /// <inheritdoc />
    public TransferPlan? TryLoad(Guid jobId)
    {
        lock (_plans)
        {
            return _plans.GetValueOrDefault(jobId);
        }
    }

    /// <inheritdoc />
    public void Delete(Guid jobId)
    {
        lock (_plans)
        {
            _plans.Remove(jobId);
        }
    }
}

/// <summary>One JSON file per job, written atomically (temp file, then rename) so a crash never leaves half a plan (spec §3.4).</summary>
public sealed class FileTransferPlanStore(IFileSystem fileSystem, string folder) : ITransferPlanStore
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    /// <inheritdoc />
    public void Save(TransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        fileSystem.Directory.CreateDirectory(folder);
        var path = PathFor(plan.JobId);
        var temp = path + ".tmp";
        fileSystem.File.WriteAllText(temp, JsonSerializer.Serialize(plan, Options));
        fileSystem.File.Move(temp, path, overwrite: true);
    }

    /// <inheritdoc />
    public TransferPlan? TryLoad(Guid jobId)
    {
        try
        {
            var path = PathFor(jobId);
            return fileSystem.File.Exists(path) ? JsonSerializer.Deserialize<TransferPlan>(fileSystem.File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Delete(Guid jobId)
    {
        try
        {
            var path = PathFor(jobId);
            if (fileSystem.File.Exists(path))
            {
                fileSystem.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover plan of a finished job is harmless; it is replaced if the id is ever reused.
        }
    }

    private string PathFor(Guid jobId) => fileSystem.Path.Combine(folder, jobId.ToString("N") + ".plan.json");
}
