using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Archives;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Providers.Archive;

/// <summary>
/// Runs <see cref="JobKind.Compress"/> (spec §15.4, §12.3): packs files and folders into a zip or 7z archive, with a level and an
/// optional password, and progress in bytes. The archive is written next to its final name as <c>.leviathan-part</c> and renamed when
/// complete (spec §3.4), so a cancelled or crashed job never leaves a half archive that looks real. A name that is taken follows the
/// job's <see cref="JobRequest.Conflict"/> rule: keep both (the default), replace or skip. Links are not followed and empty folders are
/// not stored; both are reported in the job's notes.
/// </summary>
public sealed class ArchiveCompressExecutor(IArchiveWriter writer, IFileSystem fileSystem) : IJobExecutor
{
    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind == JobKind.Compress;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var destination = LocationPath.Parse(request.Destination!);
        if (destination.Kind is not (LocationKind.Drive or LocationKind.Unc))
        {
            return new JobOutcome(JobState.Failed, "Archives can only be created on this PC or on a Windows share.");
        }

        var entries = new List<ArchiveWriteEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emptyFolders = 0;
        var links = 0;
        foreach (var source in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = LocationPath.Parse(source);
            if (location.Kind is not (LocationKind.Drive or LocationKind.Unc) || ArchiveKinds.IsInside(location))
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Skipped, "Only files and folders on this PC or on a Windows share can be compressed."));
                continue;
            }

            var path = location.ToWin32Path();
            if (fileSystem.File.Exists(path))
            {
                Add(entries, names, location.Name, path, fileSystem.FileInfo.New(path).Length);
            }
            else if (fileSystem.Directory.Exists(path))
            {
                var before = entries.Count;
                Collect(path, location.Name, entries, names, ref emptyFolders, ref links, context, cancellationToken);
                if (entries.Count == before)
                {
                    emptyFolders++;
                }
            }
            else
            {
                context.AddResult(new ItemOutcome(source, null, ItemStatus.Failed, "It is not there any more."));
            }
        }

        if (links > 0)
        {
            context.AddNote($"{links:N0} links were not followed.");
        }

        if (emptyFolders > 0)
        {
            context.AddNote($"{emptyFolders:N0} empty folders were not stored.");
        }

        if (entries.Count == 0)
        {
            return new JobOutcome(JobState.Failed, "There was nothing to compress.");
        }

        var target = destination.ToWin32Path();
        var replace = false;
        if (fileSystem.File.Exists(target))
        {
            switch (request.Conflict)
            {
                case ConflictPolicy.Skip:
                    context.AddNote($"{destination.Name} already exists; nothing was written.");
                    return JobOutcome.Completed;
                case ConflictPolicy.Replace:
                    replace = true;
                    break;
                default:
                    target = FreeName(target);
                    break;
            }
        }

        var total = entries.Sum(e => e.Size);
        context.Report(0, total, 0, entries.Count);
        var part = target + ".leviathan-part";
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(target)!);
        if (fileSystem.File.Exists(part))
        {
            fileSystem.File.Delete(part);
        }

        try
        {
            var written = 0;
            await writer.WriteAsync(
                part,
                request.ArchiveFormat,
                request.ArchiveLevel,
                request.ArchivePassword,
                entries,
                (bytes, current) => context.Report(bytes, total, Math.Min(++written, entries.Count), entries.Count, current),
                cancellationToken).ConfigureAwait(false);
            fileSystem.File.Move(part, target, overwrite: replace);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            if (ex is ArchiveException or IOException or UnauthorizedAccessException)
            {
                return new JobOutcome(JobState.Failed, ex.Message);
            }

            throw;
        }

        foreach (var source in request.Sources)
        {
            if (!context.Results.Any(r => r.Source == source))
            {
                context.AddResult(new ItemOutcome(source, target, ItemStatus.Done));
            }
        }

        context.Report(total, total, entries.Count, entries.Count);
        return JobOutcome.Completed;
    }

    // Two items with the same name (a file and a folder from different places) must not collide inside the archive.
    private static void Add(List<ArchiveWriteEntry> entries, HashSet<string> names, string name, string path, long size)
    {
        var unique = name;
        for (var i = 2; !names.Add(unique); i++)
        {
            var dot = name.LastIndexOf('.');
            unique = dot > 0 ? $"{name[..dot]} ({i}){name[dot..]}" : $"{name} ({i})";
        }

        entries.Add(new ArchiveWriteEntry(unique, path, size));
    }

    private void Collect(string folder, string nameInArchive, List<ArchiveWriteEntry> entries, HashSet<string> names, ref int emptyFolders, ref int links, JobContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<string> children;
        try
        {
            children = fileSystem.Directory.EnumerateFileSystemEntries(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.AddResult(new ItemOutcome(folder, null, ItemStatus.Failed, ex.Message));
            return;
        }

        foreach (var child in children)
        {
            var info = fileSystem.FileInfo.New(child);
            var name = nameInArchive + "/" + fileSystem.Path.GetFileName(child);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                links++;
                continue;
            }

            if (info.Attributes.HasFlag(FileAttributes.Directory))
            {
                var before = entries.Count;
                Collect(child, name, entries, names, ref emptyFolders, ref links, context, cancellationToken);
                if (entries.Count == before)
                {
                    emptyFolders++;
                }

                continue;
            }

            Add(entries, names, name, child, info.Length);
        }
    }

    private string FreeName(string path)
    {
        var folder = fileSystem.Path.GetDirectoryName(path)!;
        var stem = fileSystem.Path.GetFileNameWithoutExtension(path);
        var extension = fileSystem.Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = fileSystem.Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!fileSystem.File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (fileSystem.File.Exists(path))
            {
                fileSystem.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The part file never has a real name; the next run replaces it.
        }
    }
}
