using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Leviathan.Core.Resilience;

/// <summary>Asks the operating system which adapter reaches an address and how fast it linked. Behind an interface so the probe is testable without a network (spec §18).</summary>
public interface IAdapterTable
{
    /// <summary>The negotiated speed in bits per second of the adapter that would carry traffic to <paramref name="remote"/>, or null when unknown (no route, a virtual adapter that reports nothing).</summary>
    long? SpeedTo(IPAddress remote);
}

/// <summary>
/// <see cref="IAdapterTable"/> over the system: the route is found by connecting a UDP socket (which sends nothing and only picks the
/// local address the OS would use), the speed is that adapter's <see cref="NetworkInterface.Speed"/>.
/// </summary>
public sealed class SystemAdapterTable : IAdapterTable
{
    /// <inheritdoc />
    public long? SpeedTo(IPAddress remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        try
        {
            using var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remote, 9));
            if (socket.LocalEndPoint is not IPEndPoint { Address: var local })
            {
                return null;
            }

            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus == OperationalStatus.Up && adapter.Speed > 0
                    && adapter.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)))
                {
                    return adapter.Speed;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException or ObjectDisposedException)
        {
            return null;
        }
    }
}

/// <summary>
/// The negotiated link speed to a server (spec §3.5 item 3, shown in the status bar): resolves the host, asks which adapter reaches it,
/// remembers the answer for a minute so the status bar never asks twice for the same folder. Never throws; a link that cannot be
/// measured is simply not shown. Thread-safe.
/// </summary>
public sealed class LinkSpeedProbe
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(2);

    private readonly IHostAddressResolver _resolver;
    private readonly IAdapterTable _adapters;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, long? Speed)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the probe.</summary>
    public LinkSpeedProbe(IHostAddressResolver resolver, IAdapterTable adapters, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(adapters);
        _resolver = resolver;
        _adapters = adapters;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The speed learned for <paramref name="host"/> in the last minute (null inside the tuple when it could not be measured), or null when it has not been asked or the answer is old.</summary>
    public (long? BitsPerSecond, bool Known) Cached(string host) =>
        _cache.TryGetValue(host, out var entry) && _time.GetUtcNow() - entry.At < Lifetime ? (entry.Speed, true) : (null, false);

    /// <summary>Bits per second of the link to <paramref name="host"/>, or null when it cannot be told.</summary>
    public async Task<long?> SpeedToAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (Cached(host) is { Known: true } cached)
        {
            return cached.BitsPerSecond;
        }

        long? speed = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ResolveTimeout);
        try
        {
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal.ToString()]
                : await _resolver.ResolveAsync(host, limit.Token).ConfigureAwait(false);
            foreach (var text in addresses)
            {
                if (IPAddress.TryParse(text, out var address) && !IPAddress.IsLoopback(address))
                {
                    speed = await Task.Run(() => _adapters.SpeedTo(address), CancellationToken.None).ConfigureAwait(false);
                    if (speed is not null)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Name resolution took too long: show nothing now, ask again later.
            return null;
        }

        _cache[host] = (_time.GetUtcNow(), speed);
        return speed;
    }

    /// <summary>"1 Gb/s", "2.5 Gb/s", "100 Mb/s", "10 Gb/s": the way adapters are named.</summary>
    public static string Format(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return string.Empty;
        }

        return bitsPerSecond >= 1_000_000_000
            ? string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1e9:0.##} Gb/s")
            : string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1e6:0.##} Mb/s");
    }
}
