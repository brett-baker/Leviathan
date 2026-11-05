using System.IO.Abstractions;
using Leviathan.Core.Paths;

namespace Leviathan.Engine.Transfers;

/// <summary>One soft-deleted item in the bin (spec §12.5).</summary>
/// <param name="Original">Where it came from.</param>
/// <param name="Trashed">Where it is now.</param>
/// <param name="DeletedAt">When it was deleted.</param>
/// <param name="SizeBytes">Its size, or -1 for a folder whose size was not measured.</param>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="TrashRoot">The trash folder whose manifest lists it.</param>
public sealed record BinItem(string Original, LocationPath Trashed, DateTimeOffset DeletedAt, long SizeBytes, bool IsFolder, LocationPath TrashRoot)
{
    /// <summary>The item's name.</summary>
    public string Name => Trashed.Name;
}

/// <summary>What a scan of the bin found.</summary>
/// <param name="Items">Everything still there, newest deletion first.</param>
/// <param name="Unreachable">Trash folders that could not be read (a share that is off, no access): their items are not listed, and not purged.</param>
public sealed record BinScan(IReadOnlyList<BinItem> Items, IReadOnlyList<LocationPath> Unreachable);

/// <summary>
/// Lists the soft-deleted items across every trash folder in the <see cref="BinRegistry"/> by reading their manifests (spec §12.5),
/// so the Soft-delete bin never crawls a share. An item whose file is gone (purged by hand, restored by another tool) is left out. A
/// share that does not answer is reported, not waited for beyond the file system's own timeout, and does not hide the others.
/// </summary>
public sealed class BinScanner(BinRegistry registry)
{
    /// <summary>Reads every manifest; <paramref name="measureFolders"/> also adds up folder sizes, which the automatic purge needs and a quick listing does not.</summary>
    public BinScan Scan(bool measureFolders, CancellationToken cancellationToken)
    {
        var fs = registry.FileSystem;
        var items = new List<BinItem>();
        var unreachable = new List<LocationPath>();
        foreach (var root in registry.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<BinEntry> entries;
            try
            {
                if (!fs.Directory.Exists(root.ToWin32Path()))
                {
                    unreachable.Add(root);
                    continue;
                }

                entries = BinManifest.Read(fs, root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreachable.Add(root);
                continue;
            }

            // The same item twice (a retried write) is one item; the later line wins.
            foreach (var entry in entries.GroupBy(e => e.Trashed, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!LocationPath.TryParse(entry.Trashed, out var trashed))
                {
                    continue;
                }

                try
                {
                    var path = trashed.ToWin32Path();
                    if (fs.File.Exists(path))
                    {
                        items.Add(new BinItem(entry.Original, trashed, entry.DeletedAt, fs.FileInfo.New(path).Length, false, root));
                    }
                    else if (fs.Directory.Exists(path))
                    {
                        items.Add(new BinItem(entry.Original, trashed, entry.DeletedAt, measureFolders ? SizeOf(fs, path, cancellationToken) : -1, true, root));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable item is left out; the rest of the bin is still listed.
                }
            }
        }

        return new BinScan([.. items.OrderByDescending(i => i.DeletedAt)], unreachable);
    }

    private static long SizeOf(IFileSystem fs, string folder, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var file in fs.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += fs.FileInfo.New(file).Length;
        }

        return total;
    }
}
