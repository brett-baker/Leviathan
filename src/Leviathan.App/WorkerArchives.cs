using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Archives;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.App;

/// <summary>
/// Hands extract and compress to the worker as jobs (spec §15.4, §12.3). The window only decides what and where; the worker does the
/// unpacking and packing, journaled and with progress in the Operations panel, so closing the window never stops it. A name clash is
/// never overwritten: both are kept (the job's default).
/// </summary>
internal sealed class WorkerArchives(WorkerTransfers transfers) : IArchiveService
{
    /// <inheritdoc />
    public Task<TransferOutcome> ExtractAsync(IReadOnlyList<LocationPath> archives, LocationPath destination, bool intoSubfolder, CancellationToken cancellationToken)
    {
        var job = new JobRequest(JobKind.Extract, [.. archives.Select(a => a.Value)], destination.Value)
        {
            ExtractIntoSubfolder = intoSubfolder,
            Conflict = ConflictPolicy.KeepBoth,
        };
        var what = archives.Count == 1 ? archives[0].Name : $"{archives.Count} archives";
        return transfers.SubmitTrackedAsync(job, archives, $"Extracting {what} to {destination.Name}…", cancellationToken, destination);
    }

    /// <inheritdoc />
    public Task<TransferOutcome> CompressAsync(IReadOnlyList<LocationPath> items, LocationPath archive, CompressChoice choice, CancellationToken cancellationToken)
    {
        var job = new JobRequest(JobKind.Compress, [.. items.Select(i => i.Value)], archive.Value)
        {
            ArchiveFormat = choice.Format,
            ArchiveLevel = choice.Level,
            ArchivePassword = string.IsNullOrEmpty(choice.Password) ? null : choice.Password,
            Conflict = ConflictPolicy.KeepBoth,
        };
        return transfers.SubmitTrackedAsync(job, items, $"Compressing to {archive.Name}…", cancellationToken, archive.Parent);
    }
}
