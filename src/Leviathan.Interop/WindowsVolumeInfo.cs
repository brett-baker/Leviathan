using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace Leviathan.Interop;

/// <summary>
/// <see cref="IVolumeInfo"/> over the Win32 volume APIs. Every drive is queried on its own pool thread with a timeout, because
/// a disconnected mapped drive can block a Win32 call for a minute (spec §3.3). A drive that does not answer is listed as
/// unresponsive; its blocked call finishes harmlessly in the background.
/// </summary>
public sealed class WindowsVolumeInfo : IVolumeInfo
{
    /// <summary>How long one drive may take to answer.</summary>
    public static readonly TimeSpan DriveTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _timeout;

    /// <summary>Creates the service with the default timeout.</summary>
    public WindowsVolumeInfo()
        : this(DriveTimeout)
    {
    }

    /// <summary>Creates the service with a custom per-drive timeout.</summary>
    public WindowsVolumeInfo(TimeSpan timeout) => _timeout = timeout;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DriveDetails>> GetDrivesAsync(CancellationToken cancellationToken)
    {
        var mask = PInvoke.GetLogicalDrives();
        var roots = new List<string>();
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                roots.Add($"{(char)('A' + i)}:\\");
            }
        }

        var tasks = roots.Select(root => QueryAsync(root, cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.Where(r => r is not null).Select(r => r!)];
    }

    /// <inheritdoc />
    public Task<DriveDetails?> GetDriveAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Kind != LocationKind.Drive || location.Root.Length < 2 || !char.IsAsciiLetter(location.Root[0]))
        {
            return Task.FromResult<DriveDetails?>(null);
        }

        return QueryAsync($"{char.ToUpperInvariant(location.Root[0])}:\\", cancellationToken);
    }

    private async Task<DriveDetails?> QueryAsync(string root, CancellationToken cancellationToken)
    {
        if (!LocationPath.TryParse(root, out var location))
        {
            return null;
        }

        var work = Task.Run(() => Query(root, location), CancellationToken.None);
        try
        {
            return await work.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var kind = KindOf(root);
            return new DriveDetails(location, string.Empty, kind, StorageMedium.Unknown, string.Empty, -1, -1, NetworkPathOf(root, kind), IsResponsive: false);
        }
    }

    private static DriveDetails Query(string root, LocationPath location)
    {
        var kind = KindOf(root);
        long total = -1, free = -1;
        if (TryGetSpace(root, out var totalBytes, out var freeBytes))
        {
            total = totalBytes;
            free = freeBytes;
        }

        TryGetVolume(root, out var label, out var fileSystem);
        var medium = kind == DriveKind.Fixed ? MediumOf(root[0]) : StorageMedium.Unknown;
        return new DriveDetails(location, label, kind, medium, fileSystem, total, free, NetworkPathOf(root, kind));
    }

    private static DriveKind KindOf(string root) => PInvoke.GetDriveType(root) switch
    {
        PInvoke.DRIVE_FIXED => DriveKind.Fixed,
        PInvoke.DRIVE_REMOVABLE => DriveKind.Removable,
        PInvoke.DRIVE_REMOTE => DriveKind.Network,
        PInvoke.DRIVE_CDROM => DriveKind.Optical,
        PInvoke.DRIVE_RAMDISK => DriveKind.RamDisk,
        _ => DriveKind.Unknown,
    };

    private static bool TryGetSpace(string root, out long total, out long free)
    {
        var ok = PInvoke.GetDiskFreeSpaceEx(root, out var available, out var totalBytes, out _);
        total = ok ? (long)totalBytes : -1;
        free = ok ? (long)available : -1;
        return ok;
    }

    private static void TryGetVolume(string root, out string label, out string fileSystem)
    {
        Span<char> labelBuffer = stackalloc char[261];
        Span<char> fsBuffer = stackalloc char[261];
        if (PInvoke.GetVolumeInformation(root, labelBuffer, fsBuffer))
        {
            label = Text(labelBuffer);
            fileSystem = Text(fsBuffer);
            return;
        }

        label = string.Empty;
        fileSystem = string.Empty;
    }

    private static string Text(ReadOnlySpan<char> buffer)
    {
        var end = buffer.IndexOf((char)0);
        return new string(end >= 0 ? buffer[..end] : buffer);
    }

    private static string? NetworkPathOf(string root, DriveKind kind) =>
        kind == DriveKind.Network ? WindowsDriveMappings.QueryUnc(root) : null;

    /// <summary>Asks the storage stack whether the disk behind <paramref name="letter"/> has a seek penalty (HDD) or not (SSD).</summary>
    private static unsafe StorageMedium MediumOf(char letter)
    {
        using var handle = PInvoke.CreateFile(
            @"\\.\" + letter + ":",
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            null);
        if (handle.IsInvalid)
        {
            return StorageMedium.Unknown;
        }

        // StorageDeviceSeekPenaltyProperty = 7, PropertyStandardQuery = 0.
        var query = new STORAGE_PROPERTY_QUERY { PropertyId = (STORAGE_PROPERTY_ID)7, QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery };
        DEVICE_SEEK_PENALTY_DESCRIPTOR descriptor = default;
        var input = MemoryMarshal.AsBytes(new ReadOnlySpan<STORAGE_PROPERTY_QUERY>(in query));
        var output = MemoryMarshal.AsBytes(new Span<DEVICE_SEEK_PENALTY_DESCRIPTOR>(ref descriptor));
        var ok = PInvoke.DeviceIoControl(handle, PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, input, output, out var returned, null);
        if (!ok || returned < Unsafe.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>())
        {
            return StorageMedium.Unknown;
        }

        return descriptor.IncursSeekPenalty ? StorageMedium.Hdd : StorageMedium.Ssd;
    }
}

/// <summary>Reads which network share a mapped drive letter points at.</summary>
public static class WindowsDriveMappings
{
    /// <summary>
    /// Returns the share a mapped drive letter points at (<c>Z:</c> gives <c>\\nas\media</c>), or null for a local drive or
    /// when Windows cannot say. May block on a dead server; call it off the UI thread.
    /// </summary>
    public static string? QueryUnc(string driveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);
        Span<char> buffer = stackalloc char[512];
        var length = (uint)buffer.Length;
        var error = PInvoke.WNetGetConnection(driveRoot.TrimEnd('\\'), buffer, ref length);
        if (error != WIN32_ERROR.NO_ERROR)
        {
            return null;
        }

        var end = buffer.IndexOf((char)0);
        return new string(end >= 0 ? buffer[..end] : buffer);
    }
}
