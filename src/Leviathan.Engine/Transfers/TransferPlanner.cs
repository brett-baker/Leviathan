using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// Turns a copy or move request into a <see cref="TransferPlan"/> (spec §12.1 steps 1 and 2): enumerates the sources, resolves name
/// clashes by the job's <see cref="ConflictPolicy"/>, and picks the fastest path per file. Nothing is written. A same-volume move is a
/// rename; two ends on one SMB server use server-side copy (whatever names they were reached by, via <see cref="ServerIdentityMap"/>,
/// spec §3.5 item 5); everything else goes through the chunked copier.
/// </summary>
public sealed class TransferPlanner
{
    private readonly IFileSystem _fs;
    private readonly ServerIdentityMap? _identities;

    /// <summary>Creates the planner over <paramref name="fileSystem"/>; with <paramref name="identities"/>, names of one server count as one.</summary>
    public TransferPlanner(IFileSystem fileSystem, ServerIdentityMap? identities = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fs = fileSystem;
        _identities = identities;
    }

    /// <summary>Same drive, or the same share under the same server name: a move between them can be a rename.</summary>
    public static bool SameVolume(LocationPath a, LocationPath b) =>
        a.Kind == b.Kind && string.Equals(a.Root, b.Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when both are on one SMB server, whatever names they were reached by.</summary>
    public bool SameServer(LocationPath a, LocationPath b) =>
        a.Kind == LocationKind.Unc && b.Kind == LocationKind.Unc
        && (string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase) || _identities?.AreSame(a.Server!, b.Server!) == true);

    /// <summary>The fastest way to bring <paramref name="source"/> to <paramref name="target"/> (spec §12.1 step 2).</summary>
    public TransferPath Choose(LocationPath source, LocationPath target, bool move)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (move && SameVolume(source, target))
        {
            return TransferPath.Rename;
        }

        return SameServer(source, target) ? TransferPath.ServerSideCopy : TransferPath.ChunkedCopy;
    }

    /// <summary>Plans the job. Enumeration stops promptly when <paramref name="cancellationToken"/> fires.</summary>
    public TransferPlan Plan(Guid jobId, JobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var move = request.Kind == JobKind.Move;
        var destination = LocationPath.Parse(request.Destination!);
        var items = new List<PlanItem>();
        foreach (var text in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(PlanTopLevel(LocationPath.Parse(text), destination, move, request.Conflict, cancellationToken));
        }

        return new TransferPlan(jobId, move, request.Conflict, destination.Value, items);
    }

    private static string Io(LocationPath path) => path.ToWin32Path();

    private PlanItem PlanTopLevel(LocationPath source, LocationPath destinationFolder, bool move, ConflictPolicy conflict, CancellationToken ct)
    {
        var isFolder = _fs.Directory.Exists(Io(source));
        if (!isFolder && !_fs.File.Exists(Io(source)))
        {
            return new PlanItem(source.Value, null, ItemStatus.Failed, $"{source.Name} no longer exists.", 0, []);
        }

        var network = destinationFolder.IsNetwork;
        var renamed = new List<string>();
        var target = destinationFolder.Combine(TargetName(source.Name, network, renamed));
        var exists = _fs.File.Exists(Io(target)) || _fs.Directory.Exists(Io(target));
        var samePlace = source.Equals(target);
        if (samePlace && move)
        {
            return new PlanItem(source.Value, source.Value, ItemStatus.Skipped, "Already in this folder.", 0, []);
        }

        if (exists && !samePlace && conflict == ConflictPolicy.Skip)
        {
            return new PlanItem(source.Value, null, ItemStatus.Skipped, "Something with that name is already there.", 0, []);
        }

        // Replace merges a folder into a folder (files of the same name are overwritten). A file meeting a folder, or the other way
        // round, cannot be replaced safely, so it keeps both. Copying into its own folder always makes a new name.
        var kindMismatch = exists && isFolder != _fs.Directory.Exists(Io(target));
        if (samePlace || (exists && (conflict == ConflictPolicy.KeepBoth || kindMismatch)))
        {
            target = UniqueName(destinationFolder, network ? SafeNames.Sanitize(source.Name) : source.Name, isFolder);
            exists = false;
        }

        if (move && SameVolume(source, target) && !exists)
        {
            var size = Measure(source);
            return new PlanItem(source.Value, target.Value, ItemStatus.Done, RenamedNote(renamed), size, [new PlanStep(PlanOp.RenameItem, source.Value, target.Value, size, TransferPath.Rename)]);
        }

        var steps = new List<PlanStep>();
        if (isFolder)
        {
            PlanFolder(source, target, move, conflict, steps, network, renamed, ct);
        }
        else
        {
            steps.Add(new PlanStep(PlanOp.CopyFile, source.Value, target.Value, _fs.FileInfo.New(Io(source)).Length, Choose(source, target, move)));
        }

        return new PlanItem(source.Value, target.Value, ItemStatus.Done, RenamedNote(renamed), steps.Sum(s => s.Size), steps);
    }

    private void PlanFolder(LocationPath source, LocationPath target, bool move, ConflictPolicy conflict, List<PlanStep> steps, bool network, List<string> renamed, CancellationToken ct)
    {
        steps.Add(new PlanStep(PlanOp.CreateFolder, source.Value, target.Value, 0, TransferPath.ChunkedCopy));
        foreach (var entry in _fs.Directory.EnumerateFileSystemEntries(Io(source)))
        {
            ct.ThrowIfCancellationRequested();
            var name = _fs.Path.GetFileName(entry);
            var child = source.Combine(name);
            var childTarget = target.Combine(TargetName(name, network, renamed));
            if (_fs.Directory.Exists(entry))
            {
                PlanFolder(child, childTarget, move, conflict, steps, network, renamed, ct);
                continue;
            }

            if (_fs.File.Exists(Io(childTarget)))
            {
                if (conflict == ConflictPolicy.Skip)
                {
                    continue;
                }

                if (conflict == ConflictPolicy.KeepBoth)
                {
                    childTarget = UniqueName(target, network ? SafeNames.Sanitize(name) : name, isFolder: false);
                }
            }

            steps.Add(new PlanStep(PlanOp.CopyFile, child.Value, childTarget.Value, _fs.FileInfo.New(entry).Length, Choose(child, childTarget, move)));
        }

        if (move)
        {
            steps.Add(new PlanStep(PlanOp.RemoveFolder, source.Value, target.Value, 0, TransferPath.Rename));
        }
    }

    /// <summary>The name to write: on a network destination, a name the share would refuse is changed to the closest valid one (spec §12.2).</summary>
    private static string TargetName(string name, bool network, List<string> renamed)
    {
        if (!network || SafeNames.IsValid(name))
        {
            return name;
        }

        var safe = SafeNames.Sanitize(name);
        renamed.Add($"{name} -> {safe}");
        return safe;
    }

    private static string? RenamedNote(List<string> renamed) => renamed.Count == 0
        ? null
        : $"{renamed.Count} name(s) changed to be valid on the destination: {string.Join("; ", renamed.Take(3))}{(renamed.Count > 3 ? "; ..." : string.Empty)}";

    private long Measure(LocationPath source)
    {
        try
        {
            if (_fs.File.Exists(Io(source)))
            {
                return _fs.FileInfo.New(Io(source)).Length;
            }

            return _fs.Directory.Exists(Io(source))
                ? _fs.Directory.EnumerateFiles(Io(source), "*", SearchOption.AllDirectories).Sum(f => _fs.FileInfo.New(f).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable parts are reported when the move reaches them; the size is only for the progress bar.
            return 0;
        }
    }

    /// <summary>"name (2).ext", "name (3).ext", … the first one that is free in <paramref name="folder"/>.</summary>
    private LocationPath UniqueName(LocationPath folder, string name, bool isFolder)
    {
        var stem = isFolder ? name : _fs.Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? string.Empty : _fs.Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = folder.Combine($"{stem} ({n}){extension}");
            if (!_fs.File.Exists(Io(candidate)) && !_fs.Directory.Exists(Io(candidate)))
            {
                return candidate;
            }
        }
    }
}
