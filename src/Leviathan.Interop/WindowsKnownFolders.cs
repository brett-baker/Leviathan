using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Leviathan.Interop;

/// <summary>
/// <see cref="IKnownFolders"/> over <c>SHGetKnownFolderPath</c>, so relocated folders resolve to where Windows keeps them
/// (spec §7). Results are cached for the process: relocating a folder takes effect after a restart.
/// </summary>
public sealed unsafe class WindowsKnownFolders : IKnownFolders
{
    private readonly Dictionary<KnownFolder, LocationPath?> _cache = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public LocationPath? Get(KnownFolder folder)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(folder, out var cached))
            {
                return cached;
            }

            var resolved = Resolve(folder);
            _cache[folder] = resolved;
            return resolved;
        }
    }

    private static LocationPath? Resolve(KnownFolder folder)
    {
        var id = folder switch
        {
            KnownFolder.Desktop => PInvoke.FOLDERID_Desktop,
            KnownFolder.Documents => PInvoke.FOLDERID_Documents,
            KnownFolder.Downloads => PInvoke.FOLDERID_Downloads,
            KnownFolder.Pictures => PInvoke.FOLDERID_Pictures,
            KnownFolder.Videos => PInvoke.FOLDERID_Videos,
            KnownFolder.Music => PInvoke.FOLDERID_Music,
            KnownFolder.Home => PInvoke.FOLDERID_Profile,
            _ => throw new ArgumentOutOfRangeException(nameof(folder)),
        };

        using var path = new ComHeapPtr<char>();
        var guid = id;
        var hr = PInvoke.SHGetKnownFolderPath(&guid, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, (Windows.Win32.Foundation.PWSTR*)path.GetAddressOf());
        if (hr.Failed || path.IsNull)
        {
            return null;
        }

        var text = new string(path.Get());
        return LocationPath.TryParse(text, out var location) ? location : null;
    }
}
