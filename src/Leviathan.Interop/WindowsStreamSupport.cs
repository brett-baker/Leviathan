using System.Collections.Concurrent;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Windows.Win32;

namespace Leviathan.Interop;

/// <summary>
/// Asks the volume whether it supports named streams (<c>FILE_NAMED_STREAMS</c>), so FAT, exFAT and many NAS shares are known to
/// drop colour tags (spec §11.2). Remote protocols without streams (SFTP, FTP, NFS) are answered without asking. A share that does not
/// answer within two seconds gives "unknown", and the answers are remembered for a minute so a burst of drops asks once.
/// </summary>
public sealed class WindowsStreamSupport : IStreamSupport
{
    private const uint NamedStreams = 0x00040000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (bool? Answer, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<bool?> SupportsStreamsAsync(LocationPath folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.Kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps or LocationKind.Nfs)
        {
            return false;
        }

        var root = RootOf(folder);
        if (root is null)
        {
            return null;
        }

        if (_cache.TryGetValue(root, out var known) && DateTime.UtcNow - known.At < Remember)
        {
            return known.Answer;
        }

        bool? answer;
        try
        {
            answer = await Task.Run(() => Query(root), cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            answer = null;
        }

        _cache[root] = (answer, DateTime.UtcNow);
        return answer;
    }

    private static string? RootOf(LocationPath folder) => folder.Kind switch
    {
        LocationKind.Drive => folder.Root.TrimEnd('\\') + "\\",
        LocationKind.Unc when folder.Share is not null => @"\\" + folder.Server + "\\" + folder.Share + "\\",
        _ => null,
    };

    private static bool? Query(string root)
    {
        Span<char> label = stackalloc char[261];
        Span<char> fileSystem = stackalloc char[261];
        return PInvoke.GetVolumeInformation(root, label, out _, out _, out var flags, fileSystem) ? (flags & NamedStreams) != 0 : null;
    }
}
