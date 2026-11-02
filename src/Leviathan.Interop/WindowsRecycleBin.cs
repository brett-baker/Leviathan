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

    /// <inheritdoc />
    public void Dispose() => _pool.Dispose();

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
