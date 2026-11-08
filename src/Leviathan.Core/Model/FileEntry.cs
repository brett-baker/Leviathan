using Leviathan.Core.Paths;

namespace Leviathan.Core.Model;

/// <summary>Whether an entry is a file, a folder, or a link that should not be followed.</summary>
public enum FileEntryKind
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A folder.</summary>
    Folder,

    /// <summary>A junction, symbolic link or other reparse point. Never followed when sizing folders (spec §12.7).</summary>
    Link,
}

/// <summary>
/// Stable identity of a file on NTFS: volume serial number plus the 128-bit file ID. Survives renames and moves within
/// the volume, which is what lets tags and folder sizes follow a file (spec §11.2, §12.7).
/// </summary>
/// <param name="VolumeSerial">Volume serial number.</param>
/// <param name="FileIdHigh">High 64 bits of the file ID.</param>
/// <param name="FileIdLow">Low 64 bits of the file ID.</param>
public readonly record struct FileIdentity(ulong VolumeSerial, ulong FileIdHigh, ulong FileIdLow)
{
    /// <inheritdoc />
    public override string ToString() => $"{VolumeSerial:X16}:{FileIdHigh:X16}{FileIdLow:X16}";
}

/// <summary>One item returned by a location provider.</summary>
/// <param name="Name">File or folder name.</param>
/// <param name="Location">Full location of the item.</param>
/// <param name="Kind">File, folder or link.</param>
/// <param name="Size">Size in bytes (0 for folders; folder totals come from the folder-size service).</param>
/// <param name="Created">Creation time, UTC.</param>
/// <param name="Modified">Last write time, UTC.</param>
/// <param name="Attributes">File-system attributes.</param>
public sealed record FileEntry(
    string Name,
    LocationPath Location,
    FileEntryKind Kind,
    long Size,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    FileAttributes Attributes)
{
    /// <summary>NTFS identity when the provider can supply it cheaply; otherwise null.</summary>
    public FileIdentity? Identity { get; init; }

    /// <summary>
    /// True for a placeholder of an item a running copy or move has not finished creating here; it is drawn dimmed until the real item
    /// arrives (the "UI updates: Immediate" setting).
    /// </summary>
    public bool IsPending { get; init; }

    /// <summary>True when the item is hidden.</summary>
    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);

    /// <summary>True when the item is a system item.</summary>
    public bool IsSystem => Attributes.HasFlag(FileAttributes.System);

    /// <summary>The extension including the dot, or empty.</summary>
    public string Extension
    {
        get
        {
            if (Kind != FileEntryKind.File)
            {
                return string.Empty;
            }

            var dot = Name.LastIndexOf('.');
            return dot > 0 ? Name[dot..] : string.Empty;
        }
    }
}
