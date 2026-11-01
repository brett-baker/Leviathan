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
