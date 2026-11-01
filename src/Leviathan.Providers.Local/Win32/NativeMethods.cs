using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Leviathan.Providers.Local.Win32;

/// <summary>
/// Hand-written, source-generated P/Invoke for directory enumeration. The surface is small enough that CsWin32 is not
/// needed here; the shell/COM layer (Phase 1) will use CsWin32.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const int FindFirstExLargeFetch = 0x2;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorFileNotFound = 2;

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial SafeFindHandle FindFirstFileEx(
        string lpFileName,
        int fInfoLevelId,
        Win32FindData* lpFindFileData,
        int fSearchOp,
        nint lpSearchFilter,
        int dwAdditionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FindNextFile(SafeFindHandle hFindFile, Win32FindData* lpFindFileData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint hFindFile);
}

/// <summary>WIN32_FIND_DATAW.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Win32FindData
{
    public uint FileAttributes;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint Reserved0;
    public uint Reserved1;
    public fixed char FileName[260];
    public fixed char AlternateFileName[14];

    public readonly long FileSize => ((long)FileSizeHigh << 32) | FileSizeLow;
}

/// <summary>Owns a FindFirstFileEx handle.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
}
