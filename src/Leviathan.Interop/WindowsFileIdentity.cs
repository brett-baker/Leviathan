using Leviathan.Core.Model;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Leviathan.Interop;

/// <summary>
/// Reads the volume serial number and 128-bit file ID of a file (spec §11.2) with <c>GetFileInformationByHandleEx(FileIdInfo)</c>. The
/// handle is opened for attribute access only and shared with everyone, so a file another program has open can still be identified.
/// A file system that gives no ID (some NAS and FAT volumes) or a file that cannot be opened gives null, and the tag index then keys
/// the file by path, size and modified time instead.
/// </summary>
public sealed class WindowsFileIdentity : IFileIdentitySource
{
    private const int FileIdInfoClass = 18; // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo
    private const int BufferSize = 24;      // FILE_ID_INFO: ULONGLONG VolumeSerialNumber + FILE_ID_128 (16 bytes)

    /// <inheritdoc />
    public unsafe FileIdentity? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        using var handle = PInvoke.CreateFile(
            path,
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
            null);
        if (handle.IsInvalid)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[BufferSize];
        fixed (byte* p = buffer)
        {
            if (!PInvoke.GetFileInformationByHandleEx((Windows.Win32.Foundation.HANDLE)handle.DangerousGetHandle(),(FILE_INFO_BY_HANDLE_CLASS)FileIdInfoClass, p, BufferSize))
            {
                return null;
            }
        }

        var serial = BitConverter.ToUInt64(buffer[..8]);
        var low = BitConverter.ToUInt64(buffer.Slice(8, 8));
        var high = BitConverter.ToUInt64(buffer.Slice(16, 8));
        return low == 0 && high == 0 ? null : new FileIdentity(serial, high, low);
    }
}
