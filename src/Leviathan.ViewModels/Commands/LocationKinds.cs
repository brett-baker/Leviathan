using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Commands;

/// <summary>
/// Which kinds of location a command may work on. These were seven copies of the same pattern in the command areas; a new kind of
/// location is now decided here once. Pure, so each rule is tested without a registry.
/// </summary>
public static class LocationKinds
{
    /// <summary>A drive or a Windows share: places the whole app (checksums, space map, sync, search index, create) can work on.</summary>
    public static bool IsLocalOrShare(LocationKind kind) => kind is LocationKind.Drive or LocationKind.Unc;

    /// <summary>A drive, a share or an SFTP / FTP server: places where items can be renamed, deleted and pasted into (spec §12.5).</summary>
    public static bool IsFileOperable(LocationKind kind) => kind is LocationKind.Drive or LocationKind.Unc or LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps;

    /// <summary>A folder on a drive, or inside a share: not the list of shares of a server (<c>\\server</c>), which holds no files.</summary>
    public static bool IsFolderOnDriveOrShare(LocationPath folder) =>
        IsLocalOrShare(folder.Kind) && (folder.Kind != LocationKind.Unc || folder.Share is not null);

    /// <summary>A server (SFTP, FTP or FTPS): it has no Recycle Bin, so a delete there is permanent (spec §12.5).</summary>
    public static bool IsServer(LocationKind kind) => kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps;
}
