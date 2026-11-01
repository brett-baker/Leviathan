using System.IO.Abstractions;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.ViewModels.Navigation;

/// <summary>
/// <see cref="IEntryProbe"/> over the file system. A single existence check runs on a pool thread with a timeout, so typing
/// an address on a dead share reports "not responding" after a few seconds instead of hanging the app (spec §3.3).
/// </summary>
public sealed class FileSystemEntryProbe : IEntryProbe
{
    /// <summary>How long a check may take before the location is called unresponsive.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IFileSystem _fileSystem;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a probe.</summary>
    public FileSystemEntryProbe(IFileSystem fileSystem, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <inheritdoc />
    public async Task<FileEntryKind?> GetKindAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        try
        {
            return await Task.Run(() => Stat(location), CancellationToken.None).WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new LocationException(LocationError.Timeout, location, $"{location} is not responding.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ex as LocationException ?? LocationErrors.Wrap(ex, location);
        }
    }

    private FileEntryKind? Stat(LocationPath location)
    {
        var path = location.ToWin32Path();
        if (_fileSystem.Directory.Exists(path))
        {
            return FileEntryKind.Folder;
        }

        return _fileSystem.File.Exists(path) ? FileEntryKind.File : null;
    }
}
