using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Drives;

/// <summary>
/// What the summary card above a drive or share root says (spec §8.7). Built from the drive's details, so it is pure and tested;
/// the card in the window only displays it. The breakdown of the bar by file type, SMART health, the Recycle Bin size and the
/// tagged item count need services that come later (space map, Phase 5; tags, Phase 3) and are not shown until then.
/// </summary>
/// <param name="Title">"Development (D:)" or the share name.</param>
/// <param name="TypeText">"Local disk · NTFS · SSD", "Network share · NTFS".</param>
/// <param name="UsageText">"866 GB used · 865 GB free · 931 GB total", or why the sizes are unknown.</param>
/// <param name="UsedFraction">0 to 1 for the usage bar; 0 when unknown.</param>
/// <param name="IsLowOnSpace">True below 15% free, which gets the warning colour.</param>
public sealed record DriveSummary(string Title, string TypeText, string UsageText, double UsedFraction, bool IsLowOnSpace)
{
    /// <summary>Describes <paramref name="drive"/>.</summary>
    public static DriveSummary From(DriveDetails drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        var kind = drive.Kind switch
        {
            DriveKind.Removable => "Removable disk",
            DriveKind.Network => "Network share",
            DriveKind.Optical => "Optical drive",
            _ => "Local disk",
        };
        var parts = new List<string> { kind };
        if (drive.FileSystem.Length > 0)
        {
            parts.Add(drive.FileSystem);
        }

        var medium = drive.Medium switch
        {
            StorageMedium.Ssd => "SSD",
            StorageMedium.Hdd => "HDD",
            _ => string.Empty,
        };
        if (medium.Length > 0)
        {
            parts.Add(medium);
        }

        var usage = !drive.IsResponsive
            ? "Not responding"
            : drive.HasSizes
                ? $"{DisplayFormat.Size(drive.UsedBytes)} used · {DisplayFormat.Size(drive.FreeBytes)} free · {DisplayFormat.Size(drive.TotalBytes)} total"
                : "Size not available";
        return new DriveSummary(drive.DisplayName, string.Join(" · ", parts), usage, drive.UsedFraction, drive.IsLowOnSpace);
    }

    /// <summary>True when <paramref name="location"/> is the top of a drive or of a share, where the card is shown.</summary>
    public static bool IsDriveRoot(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.IsRoot && (location.Kind == LocationKind.Drive || (location.Kind == LocationKind.Unc && location.Share is not null));
    }
}

/// <summary>Where the list of drives whose card is hidden is kept between runs. Writes must be atomic (spec §17).</summary>
public interface IDriveCardStorage
{
    /// <summary>Reads the hidden roots; a missing or damaged file gives an empty list.</summary>
    IReadOnlyList<string> Load();

    /// <summary>Saves the hidden roots.</summary>
    void Save(IReadOnlyList<string> roots);
}

/// <summary>Which drives have their summary card hidden. The choice is remembered per drive (spec §8.7).</summary>
public sealed class DriveCardPreferences
{
    private readonly IDriveCardStorage _storage;
    private readonly HashSet<string> _hidden;

    /// <summary>Loads the saved choices from <paramref name="storage"/>.</summary>
    public DriveCardPreferences(IDriveCardStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _hidden = new HashSet<string>(storage.Load(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Raised after a choice changed.</summary>
    public event EventHandler? Changed;

    /// <summary>True when the card of <paramref name="root"/> is hidden.</summary>
    public bool IsHidden(LocationPath root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return _hidden.Contains(root.Root);
    }

    /// <summary>Hides or shows the card of <paramref name="root"/> and saves the choice.</summary>
    public void SetHidden(LocationPath root, bool hidden)
    {
        ArgumentNullException.ThrowIfNull(root);
        var changed = hidden ? _hidden.Add(root.Root) : _hidden.Remove(root.Root);
        if (changed)
        {
            _storage.Save([.. _hidden.Order(StringComparer.OrdinalIgnoreCase)]);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
