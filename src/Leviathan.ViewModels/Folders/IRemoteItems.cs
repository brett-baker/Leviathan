using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Folders;

/// <summary>
/// The few changes the window makes to a file on an SFTP, FTP or FTPS server itself, without a worker job: renaming and making a
/// folder (spec §7.1). Copying, moving and deleting go through the worker like any other transfer.
/// </summary>
public interface IRemoteItems
{
    /// <summary>True when <paramref name="location"/> is on a server this service handles.</summary>
    bool Handles(LocationPath location);

    /// <summary>True when something exists at <paramref name="location"/>.</summary>
    Task<bool> ExistsAsync(LocationPath location, CancellationToken cancellationToken);

    /// <summary>Renames <paramref name="from"/> to <paramref name="to"/> on the same server.</summary>
    Task RenameAsync(LocationPath from, LocationPath to, CancellationToken cancellationToken);

    /// <summary>Makes the folder <paramref name="location"/>.</summary>
    Task CreateFolderAsync(LocationPath location, CancellationToken cancellationToken);
}
