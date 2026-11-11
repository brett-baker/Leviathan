using System.IO.Abstractions;
using System.IO.Hashing;

namespace Leviathan.Engine.Duplicates;

/// <summary>What to look at and what to ignore (spec §15.2: filters by type and minimum size).</summary>
/// <param name="MinimumBytes">Files smaller than this are ignored. Empty files are never reported: all of them are equal.</param>
/// <param name="Extensions">Only these extensions (with or without the dot), or null for every type.</param>
/// <param name="IncludeHidden">Look at hidden and system files and folders too.</param>
public sealed record DuplicateOptions(long MinimumBytes = 1, IReadOnlyList<string>? Extensions = null, bool IncludeHidden = false);

/// <summary>Files with the same contents (spec §15.2).</summary>
/// <param name="Size">Size of each file.</param>
/// <param name="Hash">The xxHash3 that was compared, as hex; the group's identity.</param>
/// <param name="Paths">Two or more files, in the order they were found.</param>
public sealed record DuplicateGroup(long Size, string Hash, IReadOnlyList<string> Paths)
{
    /// <summary>The key the worker reports for every file of the group, so the window can put them back together.</summary>
    public string Key => $"{Size}:{Hash}";

    /// <summary>Bytes a clean-up of the group would free: every copy but one.</summary>
    public long Wasted => Size * (Paths.Count - 1);
}

/// <summary>Where the search is.</summary>
public enum DuplicateStage
{
    /// <summary>Listing folders and grouping files by size.</summary>
    Scanning,

    /// <summary>Reading the first and last megabyte of files that share a size.</summary>
    PartialHashing,

    /// <summary>Reading the whole of files that still look alike.</summary>
    FullHashing,
}

/// <summary>A progress report from <see cref="DuplicateFinder"/>.</summary>
/// <param name="Stage">What it is doing now.</param>
/// <param name="FilesScanned">Files listed so far.</param>
/// <param name="Candidates">Files that still might have a twin at this stage (the work of the stage).</param>
/// <param name="Checked">Candidates already read in this stage.</param>
/// <param name="BytesRead">Bytes read from files, all stages.</param>
/// <param name="Current">The file being read, or the folder being listed.</param>
public sealed record DuplicateProgress(DuplicateStage Stage, int FilesScanned, int Candidates, int Checked, long BytesRead, string? Current);

/// <summary>What a search found.</summary>
/// <param name="Groups">The duplicate groups, the ones that waste most space first.</param>
/// <param name="FilesScanned">Files looked at.</param>
/// <param name="Unreadable">Files and folders that could not be read, with the reason; the search went on without them.</param>
public sealed record DuplicateResult(IReadOnlyList<DuplicateGroup> Groups, int FilesScanned, IReadOnlyList<string> Unreadable);

/// <summary>
/// Finds files with identical contents (spec §15.2). Files are grouped by size first, which needs no reading; files that share a
/// size are compared by a hash of their first and last megabyte; only files still matching are read in full and hashed with xxHash3.
/// That keeps a search over a share from reading most of it. Folders are listed one at a time and read one file at a time: a disk
/// or a NAS is not helped by parallel reads. Cancellation is checked between reads. Hard links to one file are reported as
/// duplicates (the file system abstraction cannot tell them apart).
/// </summary>
public sealed class DuplicateFinder(IFileSystem fileSystem)
{
    /// <summary>The bytes read from each end of a file for the partial hash.</summary>
    public const int EdgeBytes = 1024 * 1024;

    private const int BufferBytes = 256 * 1024;

    /// <summary>Searches <paramref name="roots"/> (folders, or files to include) and returns the groups of identical files.</summary>
    public async Task<DuplicateResult> FindAsync(IReadOnlyList<string> roots, DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(options);
        var unreadable = new List<string>();
        var state = new Counters();
        var bySize = Scan(roots, options, unreadable, progress, state, cancellationToken);

        var candidates = bySize.Where(g => g.Value.Count > 1).OrderByDescending(g => g.Key).ToList();
        var groups = new List<DuplicateGroup>();
        var partialTotal = candidates.Sum(g => g.Value.Count);
        var partialDone = 0;
        var fullPending = new List<(long Size, List<string> Paths)>();

        foreach (var (size, paths) in candidates)
        {
            var byPartial = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DuplicateProgress(DuplicateStage.PartialHashing, state.Files, partialTotal, partialDone, state.Bytes, path));
                partialDone++;
                await Group(byPartial, path, size, partialOnly: true, state, unreadable, cancellationToken).ConfigureAwait(false);
            }

            foreach (var (partial, same) in byPartial.Where(p => p.Value.Count > 1))
            {
                // A file no longer than both edges together was read completely already: its partial hash is its hash.
                if (size <= 2L * EdgeBytes)
                {
                    groups.Add(new DuplicateGroup(size, partial, same));
                }
                else
                {
                    fullPending.Add((size, same));
                }
            }
        }

        var fullTotal = fullPending.Sum(p => p.Paths.Count);
        var fullDone = 0;
        foreach (var (size, paths) in fullPending)
        {
            var byFull = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DuplicateProgress(DuplicateStage.FullHashing, state.Files, fullTotal, fullDone, state.Bytes, path));
                fullDone++;
                await Group(byFull, path, size, partialOnly: false, state, unreadable, cancellationToken).ConfigureAwait(false);
            }

            groups.AddRange(byFull.Where(p => p.Value.Count > 1).Select(p => new DuplicateGroup(size, p.Key, p.Value)));
        }

        progress?.Report(new DuplicateProgress(DuplicateStage.FullHashing, state.Files, fullTotal, fullTotal, state.Bytes, null));
        return new DuplicateResult([.. groups.OrderByDescending(g => g.Wasted).ThenBy(g => g.Paths[0], StringComparer.OrdinalIgnoreCase)], state.Files, unreadable);
    }

    private sealed class Counters
    {
        public int Files { get; set; }

        public long Bytes { get; set; }
    }

    private async Task Group(Dictionary<string, List<string>> into, string path, long size, bool partialOnly, Counters state, List<string> unreadable, CancellationToken cancellationToken)
    {
        var hash = await TryHashAsync(path, size, partialOnly, state, unreadable, cancellationToken).ConfigureAwait(false);
        if (hash is null)
        {
            return;
        }

        if (!into.TryGetValue(hash, out var list))
        {
            into[hash] = list = [];
        }

        list.Add(path);
    }

    private Dictionary<long, List<string>> Scan(IReadOnlyList<string> roots, DuplicateOptions options, List<string> unreadable, IProgress<DuplicateProgress>? progress, Counters state, CancellationToken cancellationToken)
    {
        var extensions = options.Extensions is { Count: > 0 }
            ? new HashSet<string>(options.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase)
            : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<string>>();
        var pending = new Stack<string>(roots.Reverse());
        var minimum = Math.Max(1, options.MinimumBytes);

        void Take(string path, long length)
        {
            if (length < minimum || (extensions is not null && !extensions.Contains(fileSystem.Path.GetExtension(path))) || !seen.Add(fileSystem.Path.GetFullPath(path)))
            {
                return;
            }

            state.Files++;
            if (!bySize.TryGetValue(length, out var list))
            {
                bySize[length] = list = [];
            }

            list.Add(path);
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            progress?.Report(new DuplicateProgress(DuplicateStage.Scanning, state.Files, 0, 0, state.Bytes, current));
            try
            {
                if (fileSystem.File.Exists(current))
                {
                    Take(current, fileSystem.FileInfo.New(current).Length);
                    continue;
                }

                var folder = fileSystem.DirectoryInfo.New(current);
                foreach (var file in folder.EnumerateFiles())
                {
                    if (!options.IncludeHidden && (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    Take(file.FullName, file.Length);
                }

                foreach (var sub in folder.EnumerateDirectories())
                {
                    // Links to folders are skipped: they would list the same files twice, or loop.
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 || (!options.IncludeHidden && (sub.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0))
                    {
                        continue;
                    }

                    pending.Push(sub.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add($"{current}: {ex.Message}");
            }
        }

        return bySize;
    }

    private async Task<string?> TryHashAsync(string path, long size, bool partialOnly, Counters state, List<string> unreadable, CancellationToken cancellationToken)
    {
        try
        {
            var stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferBytes, useAsync: true);
            try
            {
                if (stream.Length != size)
                {
                    // It changed since it was listed: it cannot be compared with the others.
                    unreadable.Add($"{path}: changed while it was being searched");
                    return null;
                }

                var hasher = new XxHash3();
                var buffer = new byte[BufferBytes];
                if (partialOnly && size > 2L * EdgeBytes)
                {
                    await AppendAsync(stream, hasher, buffer, EdgeBytes, state, cancellationToken).ConfigureAwait(false);
                    stream.Seek(-EdgeBytes, SeekOrigin.End);
                    await AppendAsync(stream, hasher, buffer, EdgeBytes, state, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await AppendAsync(stream, hasher, buffer, size, state, cancellationToken).ConfigureAwait(false);
                }

                return Convert.ToHexString(hasher.GetCurrentHash());
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{path}: {ex.Message}");
            return null;
        }
    }

    private static async Task AppendAsync(Stream stream, XxHash3 hasher, byte[] buffer, long count, Counters state, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The file ended earlier than expected.");
            }

            hasher.Append(buffer.AsSpan(0, read));
            state.Bytes += read;
            count -= read;
        }
    }
}
