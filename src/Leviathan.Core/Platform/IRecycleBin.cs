using Leviathan.Core.Paths;

namespace Leviathan.Core.Platform;

/// <summary>
/// Sends items to the Windows Recycle Bin (spec §12.5). A seam so the delete logic is tested without the shell; the real one is
/// <c>WindowsRecycleBin</c> in Leviathan.Interop, which uses <c>IFileOperation</c> on an STA thread.
/// </summary>
public interface IRecycleBin
{
    /// <summary>Moves <paramref name="path"/> to the Recycle Bin. Throws <see cref="IOException"/> when Windows cannot (no bin on that drive, in use, denied).</summary>
    Task RecycleAsync(LocationPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Moves many items to the Recycle Bin in as few shell calls as the implementation can manage, which is what makes deleting tens of
    /// thousands of files on a local drive bearable. Returns one entry per path, in order: null when that item went to the bin, otherwise
    /// why it did not (an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>). The default does them one by one.
    /// </summary>
    async Task<IReadOnlyList<Exception?>> RecycleManyAsync(IReadOnlyList<LocationPath> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var results = new Exception?[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            try
            {
                await RecycleAsync(paths[i], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results[i] = ex;
            }
        }

        return results;
    }
}
