// The GetImage-then-copy-bitmap approach is adapted from Files (brett-baker/Files)
// src/Files.App.Storage/Windows/Helpers/WindowsStorableHelpers.Icon.cs, Copyright (c) Files Community, MPL-2.0.
// Rewritten to return raw BGRA pixels and to run on Leviathan's STA pool; see THIRD-PARTY-NOTICES.md.

using System.Runtime.InteropServices;
using Leviathan.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace Leviathan.Interop;

/// <summary>
/// <see cref="IThumbnailSource"/> over <c>IShellItemImageFactory</c>: the same thumbnails and icons Explorer shows, including
/// those from installed codecs and thumbnail handlers. Runs on dedicated STA threads and checks cancellation before it starts
/// (spec §3.2). A handler that crashes the process is the business of the Media process in Phase 3, not of this source.
/// </summary>
public sealed unsafe class ShellThumbnailSource : IThumbnailSource, IDisposable
{
    private readonly StaThreadPool _pool;

    /// <summary>Creates a source with two STA threads.</summary>
    public ShellThumbnailSource()
        : this(new StaThreadPool(2, "Leviathan thumbnails"))
    {
    }

    /// <summary>Creates a source over an existing pool (shared with other shell work).</summary>
    public ShellThumbnailSource(StaThreadPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _pool = pool;
    }

    /// <inheritdoc />
    public Task<ThumbnailImage?> CreateAsync(ThumbnailRequest request, CancellationToken cancellationToken) =>
        _pool.RunAsync(() => Create(request), cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _pool.Dispose();

    private static ThumbnailImage? Create(ThumbnailRequest request)
    {
        var hr = PInvoke.SHCreateItemFromParsingName(request.Location.ToWin32Path(), null, out IShellItemImageFactory* factory);
        if (hr.Failed)
        {
            // A path that vanished between listing and thumbnailing is not an error worth remembering.
            return hr == HRESULT.E_FAIL || IsNotFound(hr) ? null : throw Marshal.GetExceptionForHR(hr.Value) ?? new IOException("SHCreateItemFromParsingName failed.");
        }

        try
        {
            var flags = request.Mode switch
            {
                ThumbnailMode.IconOnly => SIIGBF.SIIGBF_ICONONLY,
                ThumbnailMode.CachedOnly => SIIGBF.SIIGBF_INCACHEONLY | SIIGBF.SIIGBF_BIGGERSIZEOK,
                _ => SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK,
            };

            HBITMAP bitmap = default;
            hr = factory->GetImage(new SIZE { cx = request.Size, cy = request.Size }, flags, &bitmap);
            if (hr.Failed)
            {
                // "No thumbnail" (not cached, unsupported type, handler declined) is a normal answer.
                return null;
            }

            try
            {
                return CopyPixels(bitmap);
            }
            finally
            {
                PInvoke.DeleteObject((HGDIOBJ)bitmap);
            }
        }
        finally
        {
            factory->Release();
        }
    }

    private static bool IsNotFound(HRESULT hr) =>
        hr.Value is unchecked((int)0x80070002) or unchecked((int)0x80070003) or unchecked((int)0x8007007B);

    /// <summary>
    /// Copies the 32-bit DIB section the shell returns into a tightly packed BGRA array. The shell's bitmap is already top-down,
    /// which the upright-image test in Leviathan.Interop.Tests guards; flipping it would turn every thumbnail upside-down.
    /// </summary>
    private static ThumbnailImage? CopyPixels(HBITMAP bitmap)
    {
        BITMAP info = default;
        if (PInvoke.GetObject((HGDIOBJ)bitmap, sizeof(BITMAP), &info) == 0 || info.bmBitsPixel != 32 || info.bmBits is null || info.bmWidth <= 0 || info.bmHeight <= 0)
        {
            return null;
        }

        var width = info.bmWidth;
        var height = info.bmHeight;
        var rowBytes = width * 4;
        var pixels = new byte[rowBytes * height];
        var source = (byte*)info.bmBits;
        for (var y = 0; y < height; y++)
        {
            new ReadOnlySpan<byte>(source + ((long)y * info.bmWidthBytes), rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        }

        return new ThumbnailImage(width, height, pixels);
    }
}
