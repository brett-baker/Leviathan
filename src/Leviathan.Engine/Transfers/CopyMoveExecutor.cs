using System.IO.Abstractions;
using System.Security.Cryptography;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// Copies and moves files and folders on local disks and Windows shares (spec §12.1). The job is planned first by
/// <see cref="TransferPlanner"/> (every file, its resolved name and its fastest path), then run step by step. Data goes to
/// <c>name.leviathan-part</c> and is renamed into place only when complete, so a half-copied file never looks finished. A move on one
/// volume is a rename; a move across volumes copies, checks the copy against the source by hash, and only then deletes the source.
/// Name clashes follow the job's <see cref="ConflictPolicy"/>. It does not yet resume a file after a crash (slice B3 adds the per-file
/// journal), but it never leaves a finished-looking partial file and never deletes an unverified source.
/// </summary>
public sealed class CopyMoveExecutor : IJobExecutor
{
    /// <summary>Suffix of a file still being written.</summary>
    public const string PartSuffix = ".leviathan-part";

    private readonly IFileSystem _fs;
    private readonly int _bufferSize;
    private readonly TransferPlanner _planner;
    private readonly ITransferPlanStore _plans;
    private readonly IFileCopier _copier;

    /// <summary>
    /// Creates the executor over <paramref name="fileSystem"/> with <paramref name="bufferSize"/>-byte chunks (1 MB by default).
    /// <paramref name="planStore"/> keeps each job's plan until the job finishes; <paramref name="identities"/> lets names of one
    /// SMB server count as one when choosing the path (spec §3.5 item 5); <paramref name="copier"/> moves the bytes of each file
    /// (the portable chunked copier by default).
    /// </summary>
    public CopyMoveExecutor(IFileSystem fileSystem, int bufferSize = 1024 * 1024, ITransferPlanStore? planStore = null, ServerIdentityMap? identities = null, IFileCopier? copier = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _fs = fileSystem;
        _bufferSize = bufferSize;
        _planner = new TransferPlanner(fileSystem, identities);
        _plans = planStore ?? new InMemoryTransferPlanStore();
        _copier = copier ?? new ChunkedFileCopier(fileSystem, () => BufferPolicy.Fixed(bufferSize));
    }

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind is JobKind.Copy or JobKind.Move;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var destination = LocationPath.Parse(request.Destination!);

        // Plan first (spec §12.1 step 1): every file, its resolved name and its path, before a byte moves. The plan is kept until the
        // job finishes; resuming from it, with per-file progress, is the resume journal's job (slice B3), so a restart plans afresh.
        var plan = await Task.Run(() => _planner.Plan(context.Job.Id, request, cancellationToken), cancellationToken).ConfigureAwait(false);
        _plans.Save(plan);
        var run = new Run(context, plan);
        run.Publish(null);
        if (!_fs.Directory.Exists(Io(destination)))
        {
            _plans.Delete(plan.JobId);
            return new JobOutcome(JobState.Failed, $"{destination.Name} does not exist.");
        }

        if (plan.BytesToWrite > 0 && FreeSpace(destination) is { } free && free < plan.BytesToWrite)
        {
            _plans.Delete(plan.JobId);
            return new JobOutcome(JobState.Failed, $"Not enough space on {destination.Root}: needs {plan.BytesToWrite:N0} bytes, {free:N0} free.");
        }

        var failures = new List<string>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ItemOutcome outcome;
            if (item.Status != ItemStatus.Done)
            {
                outcome = new ItemOutcome(item.Source, item.Target, item.Status, item.Note);
            }
            else
            {
                try
                {
                    foreach (var step in item.Steps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await RunStepAsync(run, step, cancellationToken).ConfigureAwait(false);
                    }

                    outcome = new ItemOutcome(item.Source, item.Target, ItemStatus.Done);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    outcome = new ItemOutcome(item.Source, null, ItemStatus.Failed, ex.Message);
                }
            }

            context.AddResult(outcome);
            run.ItemsDone++;
            run.Publish(null);
            if (outcome.Status == ItemStatus.Failed)
            {
                failures.Add($"{LocationPath.Parse(item.Source).Name}: {outcome.Error}");
            }
        }

        _plans.Delete(plan.JobId);
        return failures.Count == 0
            ? JobOutcome.Completed
            : new JobOutcome(JobState.Failed, $"{failures.Count} of {plan.Items.Count} item(s) failed. {string.Join(" ", failures.Take(3))}");
    }

    private static string Io(LocationPath path) => path.ToWin32Path();

    private long? FreeSpace(LocationPath destination)
    {
        try
        {
            return _fs.DriveInfo.New(destination.Root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            // Shares and odd roots may not answer; the copy itself will report a full disk.
            return null;
        }
    }

    private async Task RunStepAsync(Run run, PlanStep step, CancellationToken ct)
    {
        var source = LocationPath.Parse(step.Source);
        var target = LocationPath.Parse(step.Target);
        switch (step.Op)
        {
            case PlanOp.RenameItem:
                if (_fs.Directory.Exists(Io(source)))
                {
                    _fs.Directory.Move(Io(source), Io(target));
                }
                else
                {
                    _fs.File.Move(Io(source), Io(target));
                }

                run.AddBytes(step.Size);
                break;
            case PlanOp.CreateFolder:
                _fs.Directory.CreateDirectory(Io(target));
                break;
            case PlanOp.CopyFile:
                await TransferFileAsync(run, source, target, step.Path, ct).ConfigureAwait(false);
                break;
            case PlanOp.RemoveFolder:
                if (!_fs.Directory.EnumerateFileSystemEntries(Io(source)).Any())
                {
                    _fs.Directory.Delete(Io(source));
                }

                break;
        }
    }

    private async Task TransferFileAsync(Run run, LocationPath source, LocationPath target, TransferPath path, CancellationToken ct)
    {
        if (path == TransferPath.Rename)
        {
            _fs.File.Move(Io(source), Io(target), overwrite: true);
            run.AddBytes(_fs.FileInfo.New(Io(target)).Length);
            return;
        }

        run.Publish(source.Name);
        var result = await _copier.CopyAsync(
            new FileCopyRequest(Io(source), Io(target), path, ComputeSourceHash: run.Move && path != TransferPath.ServerSideCopy, run.AddBytes),
            ct).ConfigureAwait(false);

        if (run.Move)
        {
            // Across volumes the original goes only after the copy is read back and matches (spec §12.1). A server-side copy has no
            // hash made on the way, so for now it is checked by size; the verify policy of slice B4 adds a sampled hash.
            var verified = result.SourceHash is { } expected
                ? await MatchesAsync(Io(target), expected, ct).ConfigureAwait(false)
                : _fs.FileInfo.New(Io(target)).Length == result.Length;
            if (!verified)
            {
                TryDelete(Io(target));
                throw new IOException($"The copy of {source.Name} did not match the original, so the original was kept.");
            }

            _fs.File.Delete(Io(source));
        }
    }

    private async Task<bool> MatchesAsync(string path, byte[] expected, CancellationToken ct)
    {
        using var stream = _fs.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[_bufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset().AsSpan().SequenceEqual(expected);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_fs.File.Exists(path))
            {
                _fs.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .leviathan-part is harmless: it is never mistaken for a finished file.
        }
    }

    /// <summary>Progress bookkeeping for one job.</summary>
    private sealed class Run(JobContext context, TransferPlan plan)
    {
        private long _bytesDone;
        private string? _current;

        public bool Move { get; } = plan.Move;

        public int ItemsDone { get; set; }

        public void AddBytes(long bytes)
        {
            _bytesDone += bytes;
            Publish(_current);
        }

        public void Publish(string? current)
        {
            _current = current ?? _current;
            context.Report(_bytesDone, plan.BytesTotal, ItemsDone, plan.Items.Count, _current);
        }
    }
}
