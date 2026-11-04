using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Leviathan.Engine.Transfers;

namespace Leviathan.Interop;

/// <summary>
/// The Windows file copier (spec §12.1 step 2). Files planned as <see cref="TransferPath.ServerSideCopy"/> go through
/// <c>CopyFile2</c>, which lets an SMB server copy between its own shares at disk speed with no data crossing the network, and which
/// carries timestamps, attributes and alternate streams itself; everything else goes through the portable
/// <see cref="ChunkedFileCopier"/> with real alternate-stream support. Both write to <c>name.leviathan-part</c> and rename when complete.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileCopier : IFileCopier
{
    private const int RequestAborted = unchecked((int)0x800704D3);

    private readonly IFileSystem _fs;
    private readonly ChunkedFileCopier _chunked;

    /// <summary>Creates the copier over the real file system.</summary>
    public WindowsFileCopier()
        : this(new FileSystem(), new WindowsAlternateStreams())
    {
    }

    /// <summary>Creates the copier over <paramref name="fileSystem"/> using <paramref name="streams"/> for the chunked path.</summary>
    public WindowsFileCopier(IFileSystem fileSystem, IAlternateStreams streams, ThroughputMemory? memory = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fs = fileSystem;
        _chunked = new ChunkedFileCopier(fileSystem, streams: streams, memory: memory ?? new ThroughputMemory());
    }

    /// <inheritdoc />
    public async Task<FileCopyResult> CopyAsync(FileCopyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path != TransferPath.ServerSideCopy)
        {
            return await _chunked.CopyAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var part = request.Target + CopyMoveExecutor.PartSuffix;
        var length = _fs.FileInfo.New(request.Source).Length;
        try
        {
            await Task.Run(() => CopyFile2(request.Source, part, request.OnBytes, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            _fs.File.Move(part, request.Target, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        return new FileCopyResult(length, null);
    }

    private static unsafe void CopyFile2(string source, string target, Action<long> onBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = new ProgressState(onBytes);
        var handle = GCHandle.Alloc(state);
        var cancel = (int*)NativeMemory.AllocZeroed(sizeof(int));
        try
        {
            var cancelAddress = (nint)cancel;
            using var registration = cancellationToken.Register(() => *(int*)cancelAddress = 1);
            var parameters = new NativeCopy.CopyFile2ExtendedParameters
            {
                Size = (uint)sizeof(NativeCopy.CopyFile2ExtendedParameters),
                CopyFlags = 0,
                Cancel = cancel,
                ProgressRoutine = &OnProgress,
                CallbackContext = GCHandle.ToIntPtr(handle),
            };
            var result = NativeCopy.CopyFile2(source, target, &parameters);
            if (result == RequestAborted || (cancellationToken.IsCancellationRequested && result != 0))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (result != 0)
            {
                // The HRESULT is 0x8007xxxx for Win32 errors, which LocationErrors.Wrap reads to tell a network drop from a refusal.
                throw new IOException(Marshal.GetPInvokeErrorMessage(result & 0xFFFF), result);
            }
        }
        finally
        {
            NativeMemory.Free(cancel);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe uint OnProgress(nint message, nint context)
    {
        const int chunkFinished = 2;
        try
        {
            if (*(int*)message == chunkFinished && GCHandle.FromIntPtr(context).Target is ProgressState state)
            {
                // COPYFILE2_MESSAGE: Type (4) + padding (4), then uliTotalBytesTransferred at offset 72 of the chunk-finished record.
                var total = *(long*)(message + 72);
                state.OnBytes(total - state.Last);
                state.Last = total;
            }
        }
        catch (Exception)
        {
            // An exception must never cross into native code; the copy simply carries on without a progress update.
        }

        return 0; // COPYFILE2_PROGRESS_CONTINUE
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_fs.File.Exists(path))
            {
                _fs.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .leviathan-part is harmless: it is never mistaken for a finished file.
        }
    }

    private sealed class ProgressState(Action<long> onBytes)
    {
        public Action<long> OnBytes { get; } = onBytes;

        public long Last { get; set; }
    }
}

/// <summary>Alternate data streams through <c>FindFirstStreamW</c> (colour tags are stored in one, spec §11.2).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAlternateStreams : IAlternateStreams
{
    /// <inheritdoc />
    public unsafe IReadOnlyList<string> Names(string path)
    {
        var names = new List<string>();
        NativeCopy.FindStreamData data;
        var handle = NativeCopy.FindFirstStreamW(path, 0, &data, 0);
        if (handle == -1)
        {
            // No streams can be listed (a share or file system without them, or the file is gone): there is nothing to copy.
            return names;
        }

        try
        {
            do
            {
                var raw = new string(data.StreamName);
                // "::$DATA" is the file's own data; a named stream looks like ":tag:$DATA".
                if (raw.Length > 7 && raw.StartsWith(':') && raw.EndsWith(":$DATA", StringComparison.Ordinal) && !raw.StartsWith("::", StringComparison.Ordinal))
                {
                    names.Add(raw[1..^6]);
                }
            }
            while (NativeCopy.FindNextStreamW(handle, &data) != 0);
        }
        finally
        {
            NativeCopy.FindClose(handle);
        }

        return names;
    }

    /// <inheritdoc />
    public async Task CopyAsync(string source, string target, string name, CancellationToken cancellationToken)
    {
        var input = new FileStream(source + ":" + name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        try
        {
            var output = new FileStream(target + ":" + name, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            try
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await output.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeCopy
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyFile2ExtendedParameters
    {
        public uint Size;
        public uint CopyFlags;
        public int* Cancel;
        public delegate* unmanaged[Stdcall]<nint, nint, uint> ProgressRoutine;
        public nint CallbackContext;
    }

    /// <summary>WIN32_FIND_STREAM_DATA: a size and a name of up to MAX_PATH + 36 characters.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FindStreamData
    {
        public long StreamSize;
        public fixed char StreamName[296];
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CopyFile2(string existingFileName, string newFileName, CopyFile2ExtendedParameters* extendedParameters);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstStreamW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindFirstStreamW(string fileName, int infoLevel, FindStreamData* findStreamData, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextStreamW", SetLastError = true)]
    internal static partial int FindNextStreamW(nint findStream, FindStreamData* findStreamData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int FindClose(nint findFile);
}
