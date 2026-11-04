using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Core.Model;
using Leviathan.Core.Platform;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leviathan.App;

/// <summary>
/// The icon of the program that opens a file, for the list and the details rows (spec §4.3, §8.1): what Explorer shows for a
/// <c>.pdf</c> or a <c>.docx</c>. The shell is asked once per file type and size, not once per file, so a folder of 40,000 photos
/// costs one question; files whose icon belongs to the file itself (programs, shortcuts, icons) are asked about one by one. Until
/// the answer arrives the generic icon of the pack is shown. UI thread only.
/// </summary>
internal static class TypeIcons
{
    /// <summary>The pixel size asked for the 16 px icons of rows and the List layout (twice the size, so they stay sharp on a high-DPI screen).</summary>
    public const int RowSize = 32;

    private const int MaxEntries = 3000;

    // Types whose icon is stored in the file, so one answer cannot stand for all of them.
    private static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".msi", ".scr", ".url", ".cur", ".ani", ".appref-ms", ".library-ms", ".msix", ".appx", ".dll", ".cpl",
    };

    private static readonly Dictionary<string, ImageSource?> Known = [];
    private static readonly Dictionary<string, Task<ImageSource?>> Asking = [];

    /// <summary>The icon when it is already known (the file type was asked about before); false while it still has to be asked for.</summary>
    public static bool TryGet(FileEntry entry, int size, out ImageSource? icon) =>
        Known.TryGetValue(KeyOf(entry, size), out icon);

    /// <summary>Asks the shell for the icon of <paramref name="entry"/>'s type; null when it has none. Many callers asking about one type share one question.</summary>
    public static Task<ImageSource?> GetAsync(FileEntry entry, int size)
    {
        var key = KeyOf(entry, size);
        if (Known.TryGetValue(key, out var known))
        {
            return Task.FromResult(known);
        }

        if (!Asking.TryGetValue(key, out var asking))
        {
            asking = AskAsync(key, entry, size);
            Asking[key] = asking;
        }

        return asking;
    }

    private static string KeyOf(FileEntry entry, int size)
    {
        var extension = entry.Extension;
        return OwnIcon.Contains(extension) ? $"{size}|{entry.Location.Value}" : $"{size}|{extension.ToLowerInvariant()}";
    }

    private static async Task<ImageSource?> AskAsync(string key, FileEntry entry, int size)
    {
        ImageSource? icon = null;
        try
        {
            // The file-type icon only: this never opens the file, so it is safe on a network share too (spec 3.3).
            var request = new ThumbnailRequest(entry.Location, entry.Modified, entry.Size, size, ThumbnailMode.IconOnly);
            var image = await App.Host.Thumbnails.GetAsync(request, CancellationToken.None);
            if (image is not null)
            {
                var bitmap = new WriteableBitmap(image.Width, image.Height);
                using (var pixels = bitmap.PixelBuffer.AsStream())
                {
                    pixels.Write(image.Bgra.Span);
                }

                icon = bitmap;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // No icon for this type; the generic one stays.
        }

        if (Known.Count >= MaxEntries)
        {
            Known.Clear();
        }

        Known[key] = icon;
        Asking.Remove(key);
        return icon;
    }
}
