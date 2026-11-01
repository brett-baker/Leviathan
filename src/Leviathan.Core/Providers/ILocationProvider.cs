using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Core.Providers;

/// <summary>
/// One kind of storage (local disks, SMB, SFTP, NFS, cloud, archives) behind a single interface (spec §2.3).
/// Listings stream in batches so the first rows paint before a large folder has finished enumerating (spec §3.1).
/// </summary>
public interface ILocationProvider
{
    /// <summary>Short name for logs and diagnostics, e.g. "local" or "smb".</summary>
    string Name { get; }

    /// <summary>True when this provider can list <paramref name="location"/>.</summary>
    bool CanHandle(LocationPath location);

    /// <summary>
    /// Lists the immediate children of <paramref name="location"/> as a stream of batches.
    /// Implementations must honour <paramref name="cancellationToken"/> promptly, never block the caller's thread,
    /// and surface I/O failures as <see cref="LocationException"/>.
    /// </summary>
    IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, CancellationToken cancellationToken);
}

/// <summary>Options for a folder listing.</summary>
public sealed record ListingOptions
{
    /// <summary>Default options: hidden and system items excluded, batches of 256.</summary>
    public static ListingOptions Default { get; } = new();

    /// <summary>Include hidden items.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Include system items.</summary>
    public bool IncludeSystem { get; init; }

    /// <summary>Maximum entries per batch.</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>A partial batch is delivered after this long, so slow shares still show rows quickly.</summary>
    public TimeSpan MaxBatchDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Returns true when <paramref name="entry"/> should appear under these options.</summary>
    public bool Includes(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (IncludeHidden || !entry.IsHidden) && (IncludeSystem || !entry.IsSystem);
    }
}

/// <summary>A batch of entries from a listing.</summary>
/// <param name="Items">The entries in this batch.</param>
/// <param name="IsFinal">True on the last batch of the listing.</param>
public sealed record ListingBatch(IReadOnlyList<FileEntry> Items, bool IsFinal);

/// <summary>What went wrong with a location, in terms the UI can act on.</summary>
public enum LocationError
{
    /// <summary>Unclassified failure.</summary>
    Unknown,

    /// <summary>The path does not exist.</summary>
    NotFound,

    /// <summary>Access was denied.</summary>
    AccessDenied,

    /// <summary>The server or share is not reachable (or dropped mid-operation). Retry later.</summary>
    Unreachable,

    /// <summary>The share needs credentials.</summary>
    AuthenticationRequired,

    /// <summary>The operation timed out.</summary>
    Timeout,
}

/// <summary>An I/O failure with a classification the UI and the retry policy can use.</summary>
public sealed class LocationException : IOException
{
    /// <summary>Creates the exception.</summary>
    public LocationException(LocationError error, LocationPath location, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
        Location = location;
        if (inner is not null)
        {
            HResult = inner.HResult;
        }
    }

    /// <summary>The classification.</summary>
    public LocationError Error { get; }

    /// <summary>The location involved.</summary>
    public LocationPath Location { get; }

    /// <summary>True when retrying later may succeed (network drops, timeouts).</summary>
    public bool IsTransient => Error is LocationError.Unreachable or LocationError.Timeout;
}
