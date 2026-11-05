using System.IO.Abstractions;
using System.Security.Cryptography;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Transfers;

/// <summary>A finished copy that did not match its source (spec §12.1 step 6).</summary>
public sealed class VerifyFailedException(string message) : IOException(message);

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
    private readonly ITransferJournal _journal;
    private readonly IFileVerifier _verifier;
    private readonly BandwidthController? _bandwidth;
    private readonly VerifyPolicy _verifyPolicy;

    /// <summary>
    /// Creates the executor over <paramref name="fileSystem"/> with <paramref name="bufferSize"/>-byte chunks (1 MB by default).
    /// <paramref name="planStore"/> keeps each job's plan until the job finishes; <paramref name="identities"/> lets names of one
    /// SMB server count as one when choosing the path (spec §3.5 item 5); <paramref name="copier"/> moves the bytes of each file
    /// (the portable chunked copier by default); <paramref name="verifyPolicy"/> and <paramref name="verifier"/> decide and run the check
    /// of each finished file (spec §12.1 step 6); <paramref name="bandwidth"/> paces the bytes to the configured limits (spec §12.3);
    /// <paramref name="journal"/> records what is done so an interrupted job continues
    /// (spec §12.1 step 4).
    /// </summary>
    public CopyMoveExecutor(IFileSystem fileSystem, int bufferSize = 1024 * 1024, ITransferPlanStore? planStore = null, ServerIdentityMap? identities = null, IFileCopier? copier = null, ITransferJournal? journal = null, VerifyPolicy? verifyPolicy = null, IFileVerifier? verifier = null, BandwidthController? bandwidth = null)
    {
        _bandwidth = bandwidth;
        _verifyPolicy = verifyPolicy ?? new VerifyPolicy();
        _verifier = verifier ?? new HashFileVerifier(fileSystem, _verifyPolicy);
        _journal = journal ?? new InMemoryTransferJournal();
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

        // Plan first (spec §12.1 step 1): every file, its resolved name and its path, before a byte moves. The plan and a journal of
        // what is done are kept until the job ends, so a network drop, a worker restart or a reboot continues the same plan (same
        // numbered names, same steps) from where it stopped instead of planning afresh (spec §12.1 step 4).
        TransferPlan plan;
        if (_plans.TryLoad(context.Job.Id) is { } saved && Matches(saved, request))
        {
            plan = saved;
        }
        else
        {
            plan = await Task.Run(() => _planner.Plan(context.Job.Id, request, cancellationToken), cancellationToken).ConfigureAwait(false);
            plan = await ResolveClashesAsync(context, plan, request.Conflict, cancellationToken).ConfigureAwait(false);
            _journal.Delete(plan.JobId);
            _plans.Save(plan);
        }

        var progress = _journal.Load(plan.JobId);
        var run = new Run(context, plan) { Throttle = _bandwidth?.CreateThrottle(context.Job.DeviceKey, request.BytesPerSecondLimit) };
        run.Publish(null);
        if (!_fs.Directory.Exists(Io(destination)))
        {
            Forget(plan.JobId);
            return new JobOutcome(JobState.Failed, $"{destination.Name} does not exist.");
        }

        var remaining = RemainingBytesToWrite(plan, progress);
        if (remaining > 0 && FreeSpace(destination) is { } free && free < remaining)
        {
            Forget(plan.JobId);
            return new JobOutcome(JobState.Failed, $"Not enough space on {destination.Root}: needs {remaining:N0} bytes, {free:N0} free.");
        }

        var failures = new List<string>();
        var skipAll = new HashSet<ErrorKind>();
        for (var itemIndex = 0; itemIndex < plan.Items.Count; itemIndex++)
        {
            var item = plan.Items[itemIndex];
            cancellationToken.ThrowIfCancellationRequested();
            ItemOutcome outcome;
            if (progress.Items.TryGetValue(itemIndex, out var finished))
            {
                // Finished before the interruption: keep its outcome, count its bytes as done.
                run.AddBytes(item.Size);
                outcome = new ItemOutcome(item.Source, finished.Status == ItemStatus.Failed ? null : item.Target, finished.Status, finished.Error);
            }
            else if (item.Status != ItemStatus.Done)
            {
                outcome = new ItemOutcome(item.Source, item.Target, item.Status, item.Note);
            }
            else
            {
                if (item.Note is { } note)
                {
                    context.AddNote($"{LocationPath.Parse(item.Source).Name}: {note}");
                }

                try
                {
                    for (var stepIndex = 0; stepIndex < item.Steps.Count; stepIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var step = item.Steps[stepIndex];
                        if (progress.DoneSteps.Contains((itemIndex, stepIndex)) || AlreadyInPlace(step, plan.Move))
                        {
                            run.AddBytes(step.Op is PlanOp.CopyFile or PlanOp.RenameItem ? step.Size : 0);
                        }
                        else
                        {
                            progress.Confirmed.TryGetValue((itemIndex, stepIndex), out var confirmed);
                            while (true)
                            {
                                try
                                {
                                    await RunStepAsync(run, step, confirmed, bytes => _journal.FileConfirmed(plan.JobId, itemIndex, stepIndex, bytes), cancellationToken).ConfigureAwait(false);
                                    break;
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !IsTransient(ex, destination))
                                {
                                    // A reason retrying later will not fix: ask once, or skip and report (spec §12.2).
                                    if (!await ShouldRetryAsync(context, request, skipAll, item.Source, ex, cancellationToken).ConfigureAwait(false))
                                    {
                                        throw;
                                    }
                                }
                            }
                        }

                        _journal.StepDone(plan.JobId, itemIndex, stepIndex);
                    }

                    outcome = new ItemOutcome(item.Source, item.Target, ItemStatus.Done);
                }
                catch (IOException ex) when (LocationErrors.Wrap(ex, destination.IsNetwork ? destination : LocationPath.Parse(item.Source)) is { IsTransient: true } transient)
                {
                    // The network dropped: leave the plan and journal, let the host wait and run the job again from here (spec §12.2).
                    throw transient;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    outcome = new ItemOutcome(item.Source, null, ItemStatus.Failed, ex.Message);
                }
            }

            _journal.ItemFinished(plan.JobId, itemIndex, outcome.Status, outcome.Error);
            if (!context.Results.Any(r => r.Source == outcome.Source))
            {
                context.AddResult(outcome);
            }

            run.ItemsDone++;
            run.Publish(null);
            if (outcome.Status == ItemStatus.Failed)
            {
                failures.Add($"{LocationPath.Parse(item.Source).Name}: {outcome.Error}");
            }
        }

        Forget(plan.JobId);
        return failures.Count == 0
            ? JobOutcome.Completed
            : new JobOutcome(JobState.Failed, $"{failures.Count} of {plan.Items.Count} item(s) failed. {string.Join(" ", failures.Take(3))}");
    }

    private static string Io(LocationPath path) => path.ToWin32Path();

    /// <summary>The most clashes sent to the dialog; any beyond it follow its "for all" rule (spec §12.4).</summary>
    public const int MaxListedClashes = 1000;

    /// <summary>
    /// Settles the name clashes of a fresh plan before anything is copied (spec §12.4). With <see cref="ConflictPolicy.Ask"/> the job asks
    /// once, listing every clash, and waits; a condition such as "if newer" given up front needs no question. The plan is saved only
    /// after this, so a restart while the question is open asks it again.
    /// </summary>
    private async Task<TransferPlan> ResolveClashesAsync(JobContext context, TransferPlan plan, ConflictPolicy policy, CancellationToken ct)
    {
        if (!plan.Clashes.Any())
        {
            return plan;
        }

        ConflictAnswer answer;
        if (policy == ConflictPolicy.Ask)
        {
            var clashes = await Task.Run(() => _planner.DescribeClashes(plan), ct).ConfigureAwait(false);
            answer = await context.AskConflictsAsync([.. clashes.Take(MaxListedClashes)], clashes.Count, ct).ConfigureAwait(false);
        }
        else
        {
            answer = new ConflictAnswer(policy);
        }

        return await Task.Run(() => _planner.ResolveClashes(plan, answer, ct), ct).ConfigureAwait(false);
    }

    private static bool IsTransient(Exception exception, LocationPath destination) =>
        exception is IOException io && LocationErrors.Wrap(io, destination.IsNetwork ? destination : LocationPath.Parse(@"C:\")).IsTransient;

    /// <summary>Why an item failed, for the prompt and for "skip all like this".</summary>
    public static ErrorKind Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException => ErrorKind.AccessDenied,
        VerifyFailedException => ErrorKind.VerifyFailed,
        IOException io => LocationErrors.Win32CodeFromHResult(io.HResult) switch
        {
            LocationErrors.AccessDenied => ErrorKind.AccessDenied,
            39 or 112 => ErrorKind.DiskFull,
            123 or 161 or 206 => ErrorKind.InvalidName,
            _ => ErrorKind.Other,
        },
        _ => ErrorKind.Other,
    };

    /// <summary>
    /// What to do about an item that failed for a permanent reason: skip it when the job is set to skip and report or the user already
    /// chose "skip all like this" for the reason; otherwise ask, once, and wait for Skip / Retry / Skip all like this (spec §12.2).
    /// </summary>
    private static async Task<bool> ShouldRetryAsync(JobContext context, JobRequest request, HashSet<ErrorKind> skipAll, string item, Exception error, CancellationToken ct)
    {
        var kind = Classify(error);
        if (request.OnError == ErrorMode.SkipAndReport || skipAll.Contains(kind))
        {
            return false;
        }

        var decision = await context.AskAsync(kind, item, error.Message, ct).ConfigureAwait(false);
        if (decision == ErrorDecision.SkipAllLikeThis)
        {
            skipAll.Add(kind);
        }

        return decision == ErrorDecision.Retry;
    }

    /// <summary>True when a saved plan was made for this very request, so continuing it is the same job.</summary>
    private static bool Matches(TransferPlan plan, JobRequest request) =>
        plan.Move == (request.Kind == JobKind.Move)
        && plan.Conflict == request.Conflict
        && string.Equals(plan.Destination, LocationPath.Parse(request.Destination!).Value, StringComparison.Ordinal)
        && plan.Items.Select(i => i.Source).SequenceEqual(request.Sources.Select(s => LocationPath.Parse(s).Value));

    private void Forget(Guid jobId)
    {
        _plans.Delete(jobId);
        _journal.Delete(jobId);
    }

    /// <summary>Bytes still to be written: what the plan writes, less finished steps and the confirmed part of files in progress.</summary>
    private static long RemainingBytesToWrite(TransferPlan plan, TransferProgress progress)
    {
        long remaining = 0;
        for (var i = 0; i < plan.Items.Count; i++)
        {
            if (progress.Items.ContainsKey(i))
            {
                continue;
            }

            for (var s = 0; s < plan.Items[i].Steps.Count; s++)
            {
                var step = plan.Items[i].Steps[s];
                if (step.Op == PlanOp.CopyFile && step.Path != TransferPath.Rename && !progress.DoneSteps.Contains((i, s)))
                {
                    remaining += step.Size - progress.Confirmed.GetValueOrDefault((i, s));
                }
            }
        }

        return Math.Max(remaining, 0);
    }

    /// <summary>
    /// A step whose journal line was lost (a crash right after it finished) is recognised by its result: for a move, the source is
    /// gone and the target is there. Anything else is simply run again, which is safe for every step.
    /// </summary>
    private bool AlreadyInPlace(PlanStep step, bool move)
    {
        switch (step.Op)
        {
            case PlanOp.RemoveFolder:
                return !_fs.Directory.Exists(Io(LocationPath.Parse(step.Source)));
            case PlanOp.RenameItem or PlanOp.CopyFile when move:
                var source = Io(LocationPath.Parse(step.Source));
                var target = Io(LocationPath.Parse(step.Target));
                var sourceGone = !_fs.File.Exists(source) && !_fs.Directory.Exists(source);
                return sourceGone && (_fs.File.Exists(target) || _fs.Directory.Exists(target));
            default:
                return false;
        }
    }

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

    private async Task RunStepAsync(Run run, PlanStep step, long confirmed, Action<long> onConfirmed, CancellationToken ct)
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
                await TransferFileAsync(run, source, target, step.Path, confirmed, onConfirmed, ct).ConfigureAwait(false);
                break;
            case PlanOp.RemoveFolder:
                if (!_fs.Directory.EnumerateFileSystemEntries(Io(source)).Any())
                {
                    _fs.Directory.Delete(Io(source));
                }

                break;
        }
    }

    private async Task TransferFileAsync(Run run, LocationPath source, LocationPath target, TransferPath path, long confirmed, Action<long> onConfirmed, CancellationToken ct)
    {
        if (path == TransferPath.Rename)
        {
            _fs.File.Move(Io(source), Io(target), overwrite: true);
            run.AddBytes(_fs.FileInfo.New(Io(target)).Length);
            return;
        }

        run.Publish(source.Name);
        var length = _fs.FileInfo.New(Io(source)).Length;
        var method = _verifyPolicy.Decide(run.Verify, run.Move, source.IsNetwork || target.IsNetwork, length);
        var result = await _copier.CopyAsync(
            new FileCopyRequest(Io(source), Io(target), path, ComputeSourceHash: method == VerifyMethod.Full && path != TransferPath.ServerSideCopy, run.AddBytes,
                path == TransferPath.ServerSideCopy ? 0 : confirmed, onConfirmed, path == TransferPath.ServerSideCopy ? null : run.Throttle),
            ct).ConfigureAwait(false);

        // The copy is read back and compared before anything else happens (spec §12.1 step 6): a move deletes its original only after
        // this passes, so a bad copy is removed and the original is kept.
        if (method != VerifyMethod.None && !await _verifier.VerifyAsync(Io(source), Io(target), method, result.SourceHash, ct).ConfigureAwait(false))
        {
            TryDelete(Io(target));
            throw new VerifyFailedException($"The copy of {source.Name} did not match the original, so the {(run.Move ? "original was kept" : "copy was removed")}.");
        }

        if (run.Move)
        {
            _fs.File.Delete(Io(source));
        }
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

        public VerifyMode Verify { get; } = context.Job.Request.Verify;

        public Func<int, CancellationToken, ValueTask>? Throttle { get; init; }

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
