using System.Runtime.InteropServices;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Leviathan.Interop;

/// <summary>
/// <see cref="IRecycleBin"/> over the shell's <c>IFileOperation</c> (spec §12.5): the same Recycle Bin Explorer uses, so items can be
/// restored from it and Windows applies its own rules (no Recycle Bin on a removable FAT drive, size limits). Runs on a dedicated
/// STA thread, never on the UI thread (spec §3.2).
/// </summary>
public sealed unsafe class WindowsRecycleBin : IRecycleBin, IDisposable
{
    private readonly StaThreadPool _pool = new(1, "Leviathan recycle");

    /// <inheritdoc />
    public Task RecycleAsync(LocationPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _pool.RunAsync(() => Recycle(path.ToWin32Path()), cancellationToken);
    }

    /// <summary>
    /// One <c>IFileOperation</c> for the whole list: Windows plans and runs it as one operation, which is many times faster than one
    /// operation per file. What did not go is found by looking whether the item is still there, since the shell reports a failure for
    /// the operation as a whole.
    /// </summary>
    public Task<IReadOnlyList<Exception?>> RecycleManyAsync(IReadOnlyList<LocationPath> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var win32 = paths.Select(p => p.ToWin32Path()).ToList();
        return _pool.RunAsync<IReadOnlyList<Exception?>>(() => RecycleMany(win32), cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _pool.Dispose();

    private static Exception?[] RecycleMany(List<string> paths)
    {
        var results = new Exception?[paths.Count];
        if (paths.Count == 0)
        {
            return results;
        }

        var hr = PInvoke.CoCreateInstance(typeof(FileOperation).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out IFileOperation* operation);
        hr.ThrowOnFailure();
        var items = new List<nint>();
        try
        {
            operation->SetOperationFlags(FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION | FILEOPERATION_FLAGS.FOF_NOERRORUI | FILEOPERATION_FLAGS.FOF_SILENT | FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR);
            for (var i = 0; i < paths.Count; i++)
            {
                try
                {
                    PInvoke.SHCreateItemFromParsingName(paths[i], null, out IShellItem* item).ThrowOnFailure();
                    items.Add((nint)item);
                    operation->DeleteItem(item, null).ThrowOnFailure();
                }
                catch (COMException ex)
                {
                    results[i] = new IOException($"Windows could not move it to the Recycle Bin: {ex.Message}", ex);
                }
            }

            string? failure = null;
            try
            {
                operation->PerformOperations().ThrowOnFailure();
            }
            catch (COMException ex)
            {
                failure = ex.Message;
            }

            // The shell says whether anything failed, not what. An item that is still there did not go.
            for (var i = 0; i < paths.Count; i++)
            {
                if (results[i] is null && (File.Exists(paths[i]) || Directory.Exists(paths[i])))
                {
                    results[i] = new IOException(failure is null ? "Windows could not move it to the Recycle Bin." : $"Windows could not move it to the Recycle Bin: {failure}");
                }
            }
        }
        finally
        {
            foreach (var item in items)
            {
                ((IShellItem*)item)->Release();
            }

            operation->Release();
        }

        return results;
    }

    private static int Recycle(string path)
    {
        var hr = PInvoke.CoCreateInstance(typeof(FileOperation).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out IFileOperation* operation);
        hr.ThrowOnFailure();
        try
        {
            // Undo = the Recycle Bin; no confirmation or error dialogs (the app asks first and reports itself).
            operation->SetOperationFlags(FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION | FILEOPERATION_FLAGS.FOF_NOERRORUI | FILEOPERATION_FLAGS.FOF_SILENT | FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR);
            PInvoke.SHCreateItemFromParsingName(path, null, out IShellItem* item).ThrowOnFailure();
            try
            {
                operation->DeleteItem(item, null).ThrowOnFailure();
                operation->PerformOperations().ThrowOnFailure();
                operation->GetAnyOperationsAborted(out var aborted).ThrowOnFailure();
                if (aborted)
                {
                    throw new IOException("Windows could not move it to the Recycle Bin.");
                }
            }
            finally
            {
                item->Release();
            }
        }
        catch (COMException ex)
        {
            throw new IOException($"Windows could not move it to the Recycle Bin: {ex.Message}", ex);
        }
        finally
        {
            operation->Release();
        }

        return 0;
    }
}
