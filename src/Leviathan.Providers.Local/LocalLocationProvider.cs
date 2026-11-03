using System.Runtime.CompilerServices;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Providers.Local;

/// <summary>
/// Lists local drives and SMB (UNC) paths. Network paths, UNC or a mapped drive letter, get the Win32 large-fetch lister and a
/// first-batch timeout, so a dead share fails in seconds with a clear error instead of hanging the tab (spec §3.3).
/// </summary>
public sealed class LocalLocationProvider : ILocationProvider
{
    private readonly IDirectoryLister _localLister;
    private readonly IDirectoryLister _networkLister;
    private readonly Func<LocationPath, string> _toOsPath;
    private readonly TimeProvider _time;

    /// <summary>Creates the provider with the platform's best listers.</summary>
    public LocalLocationProvider()
        : this(null, null, null, null)
    {
    }

    /// <summary>Creates the provider. All parameters are optional; tests use them to map paths and inject faults.</summary>
    /// <param name="localLister">Lister for drive paths.</param>
    /// <param name="networkLister">Lister for UNC paths.</param>
    /// <param name="toOsPath">Maps a location to the path handed to the OS (default: <see cref="LocationPath.ToWin32Path"/>).</param>
    /// <param name="time">Clock.</param>
    public LocalLocationProvider(IDirectoryLister? localLister, IDirectoryLister? networkLister, Func<LocationPath, string>? toOsPath, TimeProvider? time)
    {
        _localLister = localLister ?? new ManagedDirectoryLister();
        _networkLister = networkLister ?? (OperatingSystem.IsWindows() ? new Win32DirectoryLister() : _localLister);
        _toOsPath = toOsPath ?? (l => l.ToWin32Path());
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Decides whether a drive-letter path is really a network share (a mapped drive). Such a path is listed like a UNC path. The
    /// default asks Windows; tests replace it.
    /// </summary>
    public Func<LocationPath, bool> IsMappedDrive { get; init; } = MappedDrives.IsMappedDrive;

    /// <summary>How long a network listing may take to produce its first batch before failing as unreachable.</summary>
    public TimeSpan NetworkFirstBatchTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public string Name => "local";

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) =>
        location is { Kind: LocationKind.Drive } or { Kind: LocationKind.Unc, Share: not null };

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(options);
        if (!CanHandle(location))
        {
            throw new LocationException(LocationError.Unknown, location, $"The local provider cannot list {location}.");
        }

        var osPath = _toOsPath(location);
        var isNetwork = location.IsNetwork || IsMappedDrive(location);
        var lister = isNetwork ? _networkLister : _localLister;

        using var firstBatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (isNetwork)
        {
            firstBatch.CancelAfter(NetworkFirstBatchTimeout);
        }

        var batches = Batching.StreamAsync(ct => lister.List(location, osPath, ct), location, options, _time, firstBatch.Token)
            .GetAsyncEnumerator(firstBatch.Token);
        try
        {
            var first = true;
            while (true)
            {
                bool moved;
                try
                {
                    moved = await batches.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (first && !cancellationToken.IsCancellationRequested)
                {
                    throw new LocationException(LocationError.Unreachable, location, $"{location.Server} is not responding.", new TimeoutException());
                }

                if (!moved)
                {
                    yield break;
                }

                if (first)
                {
                    first = false;

                    // Past the first batch the share is responding; only the caller's token applies from here on.
                    firstBatch.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                yield return batches.Current;
            }
        }
        finally
        {
            await batches.DisposeAsync().ConfigureAwait(false);
        }
    }
}
