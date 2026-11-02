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
}
