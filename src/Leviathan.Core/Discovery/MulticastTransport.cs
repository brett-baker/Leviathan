using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Leviathan.Core.Discovery;

/// <summary>
/// Sends one datagram to a multicast group and hands back whatever answers during a short window. Behind an interface so the discoveries
/// can be tested with canned answers (spec §18) and the real sockets with a window of a few hundred milliseconds.
/// </summary>
public interface IMulticastTransport
{
    /// <summary>
    /// Sends <paramref name="payload"/> to <paramref name="group"/> (again after a second, because UDP loses packets) and calls
    /// <paramref name="onPacket"/> with each datagram that comes back and the address it came from, until <paramref name="window"/> has
    /// passed. Network trouble ends the exchange quietly. Cancellation by the caller throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task ExchangeAsync(IPEndPoint group, byte[] payload, TimeSpan window, Action<byte[], IPAddress> onPacket, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IMulticastTransport"/> over UDP sockets: one per usable IPv4 network adapter, so a machine with Wi-Fi, Ethernet and a VPN
/// asks on all of them. The sockets bind to an ephemeral port, so no multicast group is joined and nothing needs elevation: responders
/// answer a query from a port other than 5353 (mDNS) or the probe's sender (WS-Discovery) directly to it.
/// </summary>
public sealed class UdpMulticastTransport : IMulticastTransport
{
    private static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(1);
    private const int BufferSize = 9000;

    private readonly TimeProvider _time;

    /// <summary>Creates the transport.</summary>
    public UdpMulticastTransport(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task ExchangeAsync(IPEndPoint group, byte[] payload, TimeSpan window, Action<byte[], IPAddress> onPacket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(onPacket);
        var adapters = LocalAddresses();
        await Task.WhenAll(adapters.Select(local => ExchangeOnAsync(local, group, payload, window, onPacket, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>The IPv4 addresses of adapters that are up, can multicast and are not loopback.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                addresses.AddRange(adapter.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    .Select(a => a.Address));
            }
        }
        catch (NetworkInformationException)
        {
            // No adapter list, nothing to ask on.
        }

        return addresses;
    }

    private async Task ExchangeOnAsync(IPAddress local, IPEndPoint group, byte[] payload, TimeSpan window, Action<byte[], IPAddress> onPacket, CancellationToken cancellationToken)
    {
        using var windowEnded = new CancellationTokenSource(window, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, windowEnded.Token);
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(local, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

            await socket.SendToAsync(payload, SocketFlags.None, group, linked.Token).ConfigureAwait(false);
            var resend = ResendAsync(socket, payload, group, linked.Token);

            var buffer = new byte[BufferSize];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, linked.Token).ConfigureAwait(false);
                    onPacket(buffer.AsSpan(0, received.ReceivedBytes).ToArray(), ((IPEndPoint)received.RemoteEndPoint).Address);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    // An earlier datagram bounced (ICMP); the socket is fine.
                }
            }

            await resend.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (windowEnded.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The listening window ended.
        }
        catch (SocketException)
        {
            // This adapter cannot multicast (a VPN, an adapter that just went away); the others still count.
        }
        catch (ObjectDisposedException)
        {
            // Same.
        }
    }

    private async Task ResendAsync(Socket socket, byte[] payload, IPEndPoint group, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ResendAfter, _time, cancellationToken).ConfigureAwait(false);
            await socket.SendToAsync(payload, SocketFlags.None, group, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The window ended before the second send.
        }
        catch (SocketException)
        {
            // The first send went out; this one is a bonus.
        }
        catch (ObjectDisposedException)
        {
            // Closed.
        }
    }
}
