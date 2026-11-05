using System.Collections.Concurrent;
using System.IO.Abstractions;
using Leviathan.Core.Paths;

namespace Leviathan.Engine.Transfers;

/// <summary>Whose recycle folder a soft delete goes into (spec §12.5).</summary>
public enum RecycleKind
{
    /// <summary>The Synology <c>#recycle</c> folder that DSM keeps when its Recycle Bin is on for the shared folder.</summary>
    Synology,

    /// <summary>The QNAP <c>@Recycle</c> folder.</summary>
    Qnap,

    /// <summary>Leviathan's own hidden <c>.leviathan-trash</c>.</summary>
    Leviathan,
}

/// <summary>Where one soft delete puts an item.</summary>
/// <param name="Kind">Whose folder it is.</param>
/// <param name="Folder">The folder items go into: the share's own bin, or <paramref name="TrashRoot"/> itself.</param>
/// <param name="TrashRoot">The <c>.leviathan-trash</c> folder that holds the manifest (always Leviathan's, so Restore works in every case).</param>
public sealed record RecycleFolder(RecycleKind Kind, LocationPath Folder, LocationPath TrashRoot);

/// <summary>
/// Finds the folder a network item is soft-deleted into (spec §12.5): the share's own Synology <c>#recycle</c> or QNAP
/// <c>@Recycle</c> when the NAS keeps one, so its web UI and retention rules cover the files too; otherwise Leviathan's hidden
/// <c>.leviathan-trash</c> at the highest folder on the path that can be written to, normally the share root. All of these are on
/// the same share, so the move is a rename and instant for any size. What was found at a share's root is remembered, so a delete
/// asks the server nothing but "is it still there?"; a share whose bin was switched off or removed is detected again on the next
/// delete. Thread-safe.
/// </summary>
public sealed class RecycleFolders(IFileSystem fileSystem)
{
    /// <summary>The hidden folder where Leviathan keeps what it soft-deleted, and its manifest.</summary>
    public const string TrashFolder = ".leviathan-trash";

    private static readonly (string Name, RecycleKind Kind)[] ShareBins = [("#recycle", RecycleKind.Synology), ("@Recycle", RecycleKind.Qnap)];

    private readonly ConcurrentDictionary<string, RecycleFolder> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is in (or is) a recycle folder, where "delete" must not move it into another one.</summary>
    public static bool IsInsideBin(LocationPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Segments.Any(s => s.Equals(TrashFolder, StringComparison.OrdinalIgnoreCase)
            || ShareBins.Any(b => b.Name.Equals(s, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Forgets what was found for a share (its bin was switched on or off in the NAS's settings).</summary>
    public void Forget(LocationPath share)
    {
        ArgumentNullException.ThrowIfNull(share);
        _known.TryRemove(share.Root, out _);
    }

    /// <summary>
    /// The folder that <paramref name="source"/> is soft-deleted into. Throws <see cref="IOException"/> when nothing on the share can be
    /// written to, which the delete reports for that item and leaves it alone.
    /// </summary>
    public RecycleFolder Resolve(LocationPath source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var shareRoot = LocationPath.Parse(source.Root);
        if (_known.TryGetValue(shareRoot.Root, out var known) && fileSystem.Directory.Exists(known.Folder.ToWin32Path()))
        {
            return known;
        }

        _known.TryRemove(shareRoot.Root, out _);
        foreach (var (name, kind) in ShareBins)
        {
            var bin = shareRoot.Combine(name);
            if (fileSystem.Directory.Exists(bin.ToWin32Path()))
            {
                var found = new RecycleFolder(kind, bin, EnsureTrash(shareRoot) ?? throw NothingWritable(shareRoot));
                return _known[shareRoot.Root] = found;
            }
        }

        // Leviathan's own: at the share root if it can be written there, else the highest folder below it on the way to the item that can.
        var candidate = shareRoot;
        var depth = 0;
        while (true)
        {
            if (EnsureTrash(candidate) is { } trash)
            {
                var own = new RecycleFolder(RecycleKind.Leviathan, trash, trash);
                if (depth == 0)
                {
                    _known[shareRoot.Root] = own;
                }

                return own;
            }

            if (depth >= source.Segments.Count - 1)
            {
                throw NothingWritable(shareRoot);
            }

            candidate = candidate.Combine(source.Segments[depth++]);
        }
    }

    // Creates the hidden trash folder in 'parent' when it is not there, or null when the server does not let us.
    private LocationPath? EnsureTrash(LocationPath parent)
    {
        var trash = parent.Combine(TrashFolder);
        var path = trash.ToWin32Path();
        try
        {
            if (!fileSystem.Directory.Exists(path))
            {
                fileSystem.Directory.CreateDirectory(path);

                // Hidden, so other tools and Leviathan's own listings do not show it by default (spec §12.5).
                fileSystem.DirectoryInfo.New(path).Attributes = FileAttributes.Directory | FileAttributes.Hidden;
            }

            return trash;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static IOException NothingWritable(LocationPath share) =>
        new($"Nothing on {share.Root} lets Leviathan keep deleted items, so it was left alone. Use Shift+Delete to delete it permanently.");
}
