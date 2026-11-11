using Leviathan.Core.Paths;

namespace Leviathan.Core.Platform;

/// <summary>Sync clients whose folders appear under "Internet" in the sidebar (spec §7).</summary>
public enum CloudProviderKind
{
    /// <summary>OneDrive (personal or business account).</summary>
    OneDrive,

    /// <summary>A SharePoint library synced through OneDrive.</summary>
    OneDriveCommercial,

    /// <summary>Google Drive.</summary>
    GoogleDrive,

    /// <summary>Dropbox.</summary>
    Dropbox,

    /// <summary>Box.</summary>
    Box,

    /// <summary>Synology Drive.</summary>
    SynologyDrive,

    /// <summary>iCloud Drive.</summary>
    ICloudDrive,

    /// <summary>iCloud Photos.</summary>
    ICloudPhotos,

    /// <summary>Proton Drive.</summary>
    ProtonDrive,

    /// <summary>MEGA.</summary>
    Mega,

    /// <summary>Nextcloud.</summary>
    Nextcloud,

    /// <summary>ownCloud.</summary>
    OwnCloud,

    /// <summary>Jottacloud.</summary>
    Jottacloud,

    /// <summary>Adobe Creative Cloud Files.</summary>
    AdobeCreativeCloud,

    /// <summary>Infomaniak kDrive.</summary>
    KDrive,

    /// <summary>LucidLink.</summary>
    LucidLink,

    /// <summary>Sync.com.</summary>
    SyncCom,

    /// <summary>MagentaCLOUD.</summary>
    MagentaCloud,
}

/// <summary>A cloud drive found on this PC. Leviathan never talks to the cloud itself: it browses the sync client's folder (spec D6).</summary>
/// <param name="Kind">Which sync client.</param>
/// <param name="Name">Name for the sidebar, e.g. "OneDrive - Brett".</param>
/// <param name="SyncFolder">The local folder the client keeps in sync.</param>
public sealed record CloudProviderInfo(CloudProviderKind Kind, string Name, LocationPath SyncFolder);

/// <summary>Finds the installed cloud sync clients and their folders. Detection reads the registry and config files; call it off the UI thread.</summary>
public interface ICloudProviderCatalog
{
    /// <summary>
    /// Detects cloud drives. A client whose configuration cannot be read is skipped; detection never throws for one broken
    /// client and never lets one slow client delay the others.
    /// </summary>
    Task<IReadOnlyList<CloudProviderInfo>> DetectAsync(CancellationToken cancellationToken);
}

/// <summary>How a cloud sync client is doing, as far as can be told without asking its service (spec §7 "shows sync status").</summary>
public enum CloudSyncState
{
    /// <summary>Not known (no way to tell for this client).</summary>
    Unknown,

    /// <summary>The client is running, so changes are being kept in step.</summary>
    Running,

    /// <summary>The folder is there but the client is not running, so nothing is syncing until it is started.</summary>
    NotRunning,

    /// <summary>The sync folder is gone (the client was removed, or the drive is missing).</summary>
    FolderMissing,
}

/// <summary>Tells how each cloud sync client is doing. Reads the list of running programs once per call; call it off the UI thread.</summary>
public interface ICloudSyncProbe
{
    /// <summary>The state of each client in <paramref name="providers"/>.</summary>
    IReadOnlyDictionary<CloudProviderInfo, CloudSyncState> StatesOf(IReadOnlyList<CloudProviderInfo> providers);
}

/// <summary>The programs whose running means a cloud client is syncing (spec §7, Internet).</summary>
public static class CloudClients
{
    /// <summary>Process names (no <c>.exe</c>) of a client; empty when none is known, which makes its state <see cref="CloudSyncState.Unknown"/>.</summary>
    public static IReadOnlyList<string> ProcessNamesOf(CloudProviderKind kind) => kind switch
    {
        CloudProviderKind.OneDrive or CloudProviderKind.OneDriveCommercial => ["OneDrive"],
        CloudProviderKind.GoogleDrive => ["GoogleDriveFS"],
        CloudProviderKind.Dropbox => ["Dropbox"],
        CloudProviderKind.Box => ["Box", "BoxSync"],
        CloudProviderKind.SynologyDrive => ["cloud-drive-ui", "SynologyDrive", "cloud-drive-daemon"],
        CloudProviderKind.ICloudDrive or CloudProviderKind.ICloudPhotos => ["iCloudDrive", "iCloudServices"],
        CloudProviderKind.ProtonDrive => ["ProtonDrive"],
        CloudProviderKind.Mega => ["MEGAsync"],
        CloudProviderKind.Nextcloud => ["nextcloud"],
        CloudProviderKind.OwnCloud => ["owncloud"],
        CloudProviderKind.Jottacloud => ["JottaTray", "Jottacloud"],
        CloudProviderKind.KDrive => ["kDrive"],
        _ => [],
    };

    /// <summary>Decides the state from what is known: the folder, the running programs and the client's program names.</summary>
    public static CloudSyncState Decide(CloudProviderKind kind, bool folderExists, IReadOnlySet<string> runningPrograms)
    {
        ArgumentNullException.ThrowIfNull(runningPrograms);
        if (!folderExists)
        {
            return CloudSyncState.FolderMissing;
        }

        var names = ProcessNamesOf(kind);
        return names.Count == 0 ? CloudSyncState.Unknown : names.Any(runningPrograms.Contains) ? CloudSyncState.Running : CloudSyncState.NotRunning;
    }
}
