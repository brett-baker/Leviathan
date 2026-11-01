using Leviathan.Core.Paths;

namespace Leviathan.Core.Platform;

/// <summary>Well-known per-user folders shown under "Common folders" in the sidebar (spec §7).</summary>
public enum KnownFolder
{
    /// <summary>Desktop.</summary>
    Desktop,

    /// <summary>Documents.</summary>
    Documents,

    /// <summary>Downloads.</summary>
    Downloads,

    /// <summary>Pictures.</summary>
    Pictures,

    /// <summary>Videos.</summary>
    Videos,

    /// <summary>Music.</summary>
    Music,

    /// <summary>The user profile (Home).</summary>
    Home,
}

/// <summary>
/// Resolves known folders through the Windows Known Folder IDs, so a relocated Documents folder (for example on D:) works
/// (spec §7). Resolution reads the registry or shell and may block briefly; call it off the UI thread.
/// </summary>
public interface IKnownFolders
{
    /// <summary>Returns the folder, or null when it is not available on this machine.</summary>
    LocationPath? Get(KnownFolder folder);
}

/// <summary>What kind of drive a volume is.</summary>
public enum DriveKind
{
    /// <summary>Could not be determined.</summary>
    Unknown,

    /// <summary>A fixed local disk.</summary>
    Fixed,

    /// <summary>Removable media such as a USB stick.</summary>
    Removable,

    /// <summary>A mapped network drive.</summary>
    Network,

    /// <summary>An optical drive.</summary>
    Optical,

    /// <summary>A RAM disk.</summary>
    RamDisk,
}

/// <summary>Whether a disk is solid state (spec D31: queue limits differ for SSDs and spinning disks, §8.7 shows it).</summary>
public enum StorageMedium
{
    /// <summary>Not known (network drives, virtual disks, access denied).</summary>
    Unknown,

    /// <summary>No seek penalty: an SSD.</summary>
    Ssd,

    /// <summary>Seek penalty: a spinning disk.</summary>
    Hdd,
}

/// <summary>One drive as the sidebar and the drive summary show it.</summary>
/// <param name="Root">Root location such as <c>C:\</c>.</param>
/// <param name="Label">Volume label, or empty.</param>
/// <param name="Kind">Drive kind.</param>
/// <param name="Medium">SSD or HDD.</param>
/// <param name="FileSystem">File system name such as <c>NTFS</c>, or empty.</param>
/// <param name="TotalBytes">Capacity, or -1 when unknown.</param>
/// <param name="FreeBytes">Free space available to the user, or -1 when unknown.</param>
/// <param name="NetworkPath">The share a mapped drive points at (<c>\\nas\media</c>), or null.</param>
/// <param name="IsResponsive">False when the drive did not answer in time (a dead mapped drive); sizes are then unknown.</param>
public sealed record DriveDetails(
    LocationPath Root,
    string Label,
    DriveKind Kind,
    StorageMedium Medium,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    string? NetworkPath = null,
    bool IsResponsive = true)
{
    /// <summary>Free space below this share of capacity gets the warning colour (spec §8.7).</summary>
    public const double LowSpaceFraction = 0.15;

    /// <summary>True when capacity and free space are known.</summary>
    public bool HasSizes => TotalBytes > 0 && FreeBytes >= 0;

    /// <summary>Used bytes, or -1 when unknown.</summary>
    public long UsedBytes => HasSizes ? TotalBytes - FreeBytes : -1;

    /// <summary>Free share from 0 to 1, or 0 when unknown.</summary>
    public double FreeFraction => HasSizes ? (double)FreeBytes / TotalBytes : 0;

    /// <summary>Used share from 0 to 1 for the usage bar, or 0 when unknown.</summary>
    public double UsedFraction => HasSizes ? 1 - FreeFraction : 0;

    /// <summary>True when the drive is almost full.</summary>
    public bool IsLowOnSpace => HasSizes && FreeFraction < LowSpaceFraction;

    /// <summary>Name for the sidebar: "Label (C:)" or "Local Disk (C:)".</summary>
    public string DisplayName
    {
        get
        {
            var letter = Root.Value.TrimEnd('\\');
            var name = Label.Length > 0 ? Label : Kind switch
            {
                DriveKind.Removable => "Removable Disk",
                DriveKind.Network => "Network Drive",
                DriveKind.Optical => "DVD Drive",
                _ => "Local Disk",
            };
            return $"{name} ({letter})";
        }
    }
}

/// <summary>Lists the drives of the machine with their usage (spec §7, §8.7).</summary>
public interface IVolumeInfo
{
    /// <summary>
    /// Lists drives. Each drive is queried independently with a timeout so a dead mapped drive appears as unresponsive
    /// instead of delaying the others or the caller (spec §3.3).
    /// </summary>
    Task<IReadOnlyList<DriveDetails>> GetDrivesAsync(CancellationToken cancellationToken);
}
