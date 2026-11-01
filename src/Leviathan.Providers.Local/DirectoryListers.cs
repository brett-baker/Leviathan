using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Providers.Local.Win32;

namespace Leviathan.Providers.Local;

/// <summary>A blocking enumeration of one folder's children.</summary>
public interface IDirectoryLister
{
    /// <summary>Lists <paramref name="osPath"/>, producing entries whose locations are children of <paramref name="location"/>.</summary>
    IEnumerable<FileEntry> List(LocationPath location, string osPath, CancellationToken cancellationToken);
}

/// <summary>
/// .NET's file-system enumerator. On Windows it calls NtQueryDirectoryFile directly and is the fastest option for local
/// disks; it also runs on Linux, which lets the provider be tested in CI.
/// </summary>
public sealed class ManagedDirectoryLister : IDirectoryLister
{
    /// <inheritdoc />
    public IEnumerable<FileEntry> List(LocationPath location, string osPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        var enumerable = new FileSystemEnumerable<(string Name, FileAttributes Attributes, long Size, DateTimeOffset Created, DateTimeOffset Modified)>(
            osPath,
            static (ref FileSystemEntry e) => (e.FileName.ToString(), e.Attributes, e.Length, e.CreationTimeUtc, e.LastWriteTimeUtc),
            options);

        foreach (var item in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToEntry(location, item.Name, item.Attributes, item.Size, item.Created, item.Modified);
        }
    }

    internal static FileEntry ToEntry(LocationPath parent, string name, FileAttributes attributes, long size, DateTimeOffset created, DateTimeOffset modified)
    {
        var kind = attributes.HasFlag(FileAttributes.ReparsePoint)
            ? FileEntryKind.Link
            : attributes.HasFlag(FileAttributes.Directory) ? FileEntryKind.Folder : FileEntryKind.File;
        return new FileEntry(name, parent.Combine(name), kind, kind == FileEntryKind.File ? size : 0, created, modified, attributes);
    }
}

/// <summary>
/// FindFirstFileExW with <c>FindExInfoBasic</c> and <c>FIND_FIRST_EX_LARGE_FETCH</c> (spec §2.4). The large fetch asks the
/// SMB server for bigger directory pages, which cuts round trips on network shares.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32DirectoryLister : IDirectoryLister
{
    /// <inheritdoc />
    public IEnumerable<FileEntry> List(LocationPath location, string osPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrEmpty(osPath);
        var pattern = osPath.EndsWith('\\') ? osPath + "*" : osPath + "\\*";
        using var handle = FindFirst(pattern, out var first, out var error);
        if (handle.IsInvalid)
        {
            if (error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorNoMoreFiles)
            {
                yield break;
            }

            throw new LocationException(LocationErrors.FromWin32(error), location, $"Could not list {location}: {LocationErrors.Win32Name(error)}.", new IOException(null, unchecked((int)0x80070000) | error));
        }

        var current = first;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ToEntry(location, current) is { } entry)
            {
                yield return entry;
            }

            if (!FindNext(handle, out current, out error))
            {
                if (error == NativeMethods.ErrorNoMoreFiles)
                {
                    yield break;
                }

                throw new LocationException(LocationErrors.FromWin32(error), location, $"Listing {location} failed: {LocationErrors.Win32Name(error)}.", new IOException(null, unchecked((int)0x80070000) | error));
            }
        }
    }

    private static unsafe SafeFindHandle FindFirst(string pattern, out Win32FindData data, out int error)
    {
        Win32FindData local;
        var handle = NativeMethods.FindFirstFileEx(pattern, NativeMethods.FindExInfoBasic, &local, NativeMethods.FindExSearchNameMatch, 0, NativeMethods.FindFirstExLargeFetch);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        data = local;
        return handle;
    }

    private static unsafe bool FindNext(SafeFindHandle handle, out Win32FindData data, out int error)
    {
        Win32FindData local;
        var ok = NativeMethods.FindNextFile(handle, &local);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        data = local;
        return ok;
    }

    private static unsafe FileEntry? ToEntry(LocationPath parent, Win32FindData data)
    {
        var name = new string(data.FileName);
        if (name is "." or "..")
        {
            return null;
        }

        return ManagedDirectoryLister.ToEntry(
            parent,
            name,
            (FileAttributes)data.FileAttributes,
            data.FileSize,
            DateTimeOffset.FromFileTime(data.CreationTime).ToUniversalTime(),
            DateTimeOffset.FromFileTime(data.LastWriteTime).ToUniversalTime());
    }
}
