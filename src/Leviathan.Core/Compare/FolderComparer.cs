using System.IO.Abstractions;
using System.IO.Enumeration;
using Leviathan.Core.Hashing;

namespace Leviathan.Core.Compare;

/// <summary>How two items with the same name differ, or that they do not (spec §15.5).</summary>
public enum CompareStatus
{
    /// <summary>Same name, same size and the same time (within the tolerance), or the same contents when comparing by checksum.</summary>
    Identical,

    /// <summary>Only in the left folder.</summary>
    LeftOnly,

    /// <summary>Only in the right folder.</summary>
    RightOnly,

    /// <summary>In both, and the left one was changed later.</summary>
    NewerLeft,

    /// <summary>In both, and the right one was changed later.</summary>
    NewerRight,

    /// <summary>
    /// In both, with the same time but not the same size or contents: neither is newer, so a sync cannot choose between them (spec §15.5:
    /// "conflicts flagged").
    /// </summary>
    Different,

    /// <summary>A file on one side and a folder on the other.</summary>
    TypeMismatch,
}

/// <summary>How the files of two folders are told apart.</summary>
public enum CompareBy
{
    /// <summary>Name, size and modified time: fast, reads no file.</summary>
    NameSizeDate,

    /// <summary>Name, then size, then the contents of files whose sizes match (xxHash3): slower, but a touched file is not a changed file.</summary>
    Checksum,
}

/// <summary>Settings of a comparison (spec §15.5).</summary>
public sealed record CompareOptions
{
    /// <summary>How files are told apart.</summary>
    public CompareBy By { get; init; } = CompareBy.NameSizeDate;

    /// <summary>
    /// Times closer than this count as the same. Two seconds covers FAT's coarse clock; a NAS that rounds to the minute needs more. Daylight
    /// saving shifts of exactly an hour can be tolerated with <see cref="IgnoreDaylightSaving"/>.
    /// </summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Also count times that differ by exactly one hour (plus or minus the tolerance) as the same: the same file on a drive that does not follow daylight saving.</summary>
    public bool IgnoreDaylightSaving { get; init; }

    /// <summary>Names (with <c>*</c> and <c>?</c>) that are not compared, in either folder. The defaults leave out Leviathan's own work files and recycle folders.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = DefaultExclude;

    /// <summary>Also compare hidden and system items.</summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>The names left out by default: bins, Leviathan's part files and trash, and Windows' own folders.</summary>
    public static IReadOnlyList<string> DefaultExclude { get; } = ["#recycle", "@eaDir", ".leviathan-trash", "*.leviathan-part", "$RECYCLE.BIN", "System Volume Information", "Thumbs.db", "desktop.ini"];
}

/// <summary>One name found in at least one of the two folders.</summary>
/// <param name="Size">Size in bytes (0 for folders).</param>
/// <param name="Modified">Last write time, UTC.</param>
public readonly record struct CompareSide(long Size, DateTimeOffset Modified);

/// <summary>The comparison of one relative path.</summary>
/// <param name="Path">The path below the two folders, with backslashes.</param>
/// <param name="IsDirectory">True when it is a folder (on the side or sides that have it; see <see cref="CompareStatus.TypeMismatch"/>).</param>
/// <param name="Left">What the left folder has, or null.</param>
/// <param name="Right">What the right folder has, or null.</param>
/// <param name="Status">How they differ.</param>
/// <param name="LeftChecksum">The xxHash3 of the left file when it was read, else null.</param>
/// <param name="RightChecksum">The xxHash3 of the right file when it was read, else null.</param>
public sealed record CompareEntry(string Path, bool IsDirectory, CompareSide? Left, CompareSide? Right, CompareStatus Status, string? LeftChecksum = null, string? RightChecksum = null);

/// <summary>What a comparison found.</summary>
/// <param name="Entries">Every path in either folder, in name order (a folder before what is in it).</param>
/// <param name="Problems">Things that could not be read, such as a folder that denied access; the comparison goes on without them.</param>
public sealed record CompareResult(IReadOnlyList<CompareEntry> Entries, IReadOnlyList<string> Problems)
{
    /// <summary>How many entries have <paramref name="status"/>.</summary>
    public int Count(CompareStatus status) => Entries.Count(e => e.Status == status);
}

/// <summary>Where a comparison is.</summary>
/// <param name="Scanned">Items listed so far in both folders.</param>
/// <param name="Hashed">Files read so far (checksum mode).</param>
/// <param name="ToHash">Files that need reading in all, known once listing is done; -1 before.</param>
/// <param name="Current">The file being read, or null.</param>
public sealed record CompareProgress(int Scanned, int Hashed, int ToHash, string? Current);

/// <summary>
/// Compares two folders (spec §15.5): the files and folders of each, matched by relative path without regard to case, and told apart by
/// name, size and time or, for files of the same size, by contents. Nothing is changed. The listing of the two sides runs at once; the
/// comparison of a folder with 100,000 items on a share is bound by the listing, not by this class. Links are not followed. A folder that
/// cannot be read is noted in <see cref="CompareResult.Problems"/> and the comparison goes on, so one locked folder does not hide the rest.
/// </summary>
public sealed class FolderComparer(IFileSystem fileSystem)
{
    private sealed record Node(bool IsDirectory, long Size, DateTimeOffset Modified);

    /// <summary>Compares <paramref name="left"/> with <paramref name="right"/> (OS paths of existing folders).</summary>
    public async Task<CompareResult> CompareAsync(string left, string right, CompareOptions options, IProgress<CompareProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(left);
        ArgumentException.ThrowIfNullOrEmpty(right);
        ArgumentNullException.ThrowIfNull(options);
        var problems = new List<string>();
        var scanned = 0;
        void Tick() => progress?.Report(new CompareProgress(Interlocked.Increment(ref scanned), 0, -1, null));

        var leftTask = Task.Run(() => List(left, options, problems, Tick, cancellationToken), cancellationToken);
        var rightTask = Task.Run(() => List(right, options, problems, Tick, cancellationToken), cancellationToken);
        var sides = await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);
        var (l, r) = (sides[0], sides[1]);

        var entries = new List<CompareEntry>(Math.Max(l.Count, r.Count));
        var toHash = new List<int>();
        foreach (var path in l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            l.TryGetValue(path, out var a);
            r.TryGetValue(path, out var b);
            var entry = Classify(path, a, b, options);
            if (options.By == CompareBy.Checksum && entry.Status != CompareStatus.TypeMismatch && a is { IsDirectory: false } && b is { IsDirectory: false } && a.Size == b.Size)
            {
                toHash.Add(entries.Count);
            }

            entries.Add(entry);
        }

        var hashed = 0;
        foreach (var index in toHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            progress?.Report(new CompareProgress(scanned, hashed, toHash.Count, entry.Path));
            try
            {
                var sums = await Task.WhenAll(Hash(Combine(left, entry.Path), cancellationToken), Hash(Combine(right, entry.Path), cancellationToken)).ConfigureAwait(false);
                entries[index] = ByContents(entry, sums[0], sums[1], options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (problems)
                {
                    problems.Add($"{entry.Path}: {ex.Message}");
                }
            }

            hashed++;
        }

        progress?.Report(new CompareProgress(scanned, hashed, toHash.Count, null));
        return new CompareResult(entries, problems);
    }

    private static CompareEntry Classify(string path, Node? a, Node? b, CompareOptions options)
    {
        var isDirectory = (a ?? b)!.IsDirectory;
        var left = a is null ? (CompareSide?)null : new CompareSide(a.Size, a.Modified);
        var right = b is null ? (CompareSide?)null : new CompareSide(b.Size, b.Modified);
        if (a is null)
        {
            return new CompareEntry(path, isDirectory, null, right, CompareStatus.RightOnly);
        }

        if (b is null)
        {
            return new CompareEntry(path, isDirectory, left, null, CompareStatus.LeftOnly);
        }

        if (a.IsDirectory != b.IsDirectory)
        {
            return new CompareEntry(path, isDirectory, left, right, CompareStatus.TypeMismatch);
        }

        if (a.IsDirectory)
        {
            return new CompareEntry(path, true, left, right, CompareStatus.Identical);
        }

        var sameTime = SameTime(a.Modified, b.Modified, options);
        var status = sameTime
            ? (a.Size == b.Size ? CompareStatus.Identical : CompareStatus.Different)
            : a.Modified > b.Modified ? CompareStatus.NewerLeft : CompareStatus.NewerRight;
        return new CompareEntry(path, false, left, right, status);
    }

    // Same size and sums read: equal contents make the pair identical whatever the times say; different contents keep the times' verdict,
    // or are flagged when the times cannot say which is newer.
    private static CompareEntry ByContents(CompareEntry entry, string left, string right, CompareOptions options)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return entry with { Status = CompareStatus.Identical, LeftChecksum = left, RightChecksum = right };
        }

        var same = SameTime(entry.Left!.Value.Modified, entry.Right!.Value.Modified, options);
        var status = same ? CompareStatus.Different : entry.Left.Value.Modified > entry.Right.Value.Modified ? CompareStatus.NewerLeft : CompareStatus.NewerRight;
        return entry with { Status = status, LeftChecksum = left, RightChecksum = right };
    }

    private static bool SameTime(DateTimeOffset a, DateTimeOffset b, CompareOptions options)
    {
        var gap = (a - b).Duration();
        if (gap <= options.TimeTolerance)
        {
            return true;
        }

        return options.IgnoreDaylightSaving && (gap - TimeSpan.FromHours(1)).Duration() <= options.TimeTolerance;
    }

    private async Task<string> Hash(string path, CancellationToken cancellationToken)
    {
        using var stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sums = await FileChecksums.ComputeAsync(stream, [ChecksumKind.XxHash3], null, cancellationToken).ConfigureAwait(false);
        return sums[ChecksumKind.XxHash3];
    }

    private string Combine(string root, string relative) => fileSystem.Path.Combine(root, relative);

    private Dictionary<string, Node> List(string root, CompareOptions options, List<string> problems, Action tick, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Folder, string Relative)>();
        pending.Push((root, string.Empty));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, relative) = pending.Pop();
            IEnumerable<IFileSystemInfo> children;
            try
            {
                children = fileSystem.DirectoryInfo.New(folder).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (problems)
                {
                    problems.Add($"{(relative.Length == 0 ? folder : relative)}: {ex.Message}");
                }

                continue;
            }

            foreach (var child in children)
            {
                if (options.Exclude.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, child.Name, ignoreCase: true)))
                {
                    continue;
                }

                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint) || (!options.IncludeHidden && child.Attributes.HasFlag(FileAttributes.Hidden)))
                {
                    continue;
                }

                var path = relative.Length == 0 ? child.Name : relative + "\\" + child.Name;
                var isDirectory = child.Attributes.HasFlag(FileAttributes.Directory);
                found[path] = new Node(isDirectory, isDirectory ? 0 : ((IFileInfo)child).Length, new DateTimeOffset(child.LastWriteTimeUtc, TimeSpan.Zero));
                tick();
                if (isDirectory)
                {
                    pending.Push((child.FullName, path));
                }
            }
        }

        return found;
    }
}
