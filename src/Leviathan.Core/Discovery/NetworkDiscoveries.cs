using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace Leviathan.Core.Discovery;

/// <summary>
/// Finds devices that advertise <c>_smb._tcp</c> over multicast DNS (spec §7.1). Synology and QNAP boxes do, as do Macs and Samba
/// servers running Avahi. One PTR query goes out per adapter; the SRV and A records usually ride along in the same answer.
/// </summary>
public sealed class MdnsDiscovery : IDeviceDiscovery
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    private readonly IMulticastTransport _transport;

    /// <summary>Creates the discovery. <paramref name="window"/> is how long it listens (default 4 s).</summary>
    public MdnsDiscovery(IMulticastTransport transport, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        Window = window ?? TimeSpan.FromSeconds(4);
    }

    /// <summary>How long to listen for answers.</summary>
    public TimeSpan Window { get; }

    /// <inheritdoc />
    public string Name => "mdns";

    /// <inheritdoc />
    public Task DiscoverAsync(Action<DiscoveredDevice> found, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(found);
        return _transport.ExchangeAsync(
            Group,
            DnsPacket.BuildQuery(SmbAdvertisement.Service, DnsRecordType.Ptr),
            Window,
            (packet, _) =>
            {
                foreach (var device in SmbAdvertisement.Devices(DnsPacket.Parse(packet)))
                {
                    found(device);
                }
            },
            cancellationToken);
    }
}

/// <summary>A computer that answered a WS-Discovery probe.</summary>
/// <param name="Host">Host of its first address (<c>XAddrs</c>), IPv4 or a name.</param>
public sealed record WsMatch(string Host);

/// <summary>The WS-Discovery messages (SOAP-over-UDP, OASIS WS-Discovery 1.1) the Network section needs: probe for computers, read who answers.</summary>
public static class WsDiscoveryMessage
{
    private const string Discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    private const int MaxMessage = 65_536;

    /// <summary>A Probe for <c>pub:Computer</c>, the type Windows PCs, Samba's wsdd and NAS boxes announce for file sharing.</summary>
    public static byte[] BuildProbe(Guid messageId) => System.Text.Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:wsd="{Discovery}" xmlns:pub="http://schemas.microsoft.com/windows/pub/2005/07">
          <soap:Header>
            <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
            <wsa:Action>{Discovery}/Probe</wsa:Action>
            <wsa:MessageID>urn:uuid:{messageId}</wsa:MessageID>
          </soap:Header>
          <soap:Body>
            <wsd:Probe>
              <wsd:Types>pub:Computer</wsd:Types>
            </wsd:Probe>
          </soap:Body>
        </soap:Envelope>
        """);

    /// <summary>
    /// The computers a ProbeMatches message names. Anything that is not a well-formed ProbeMatches, or whose type is not a computer, gives
    /// an empty list. DTDs are refused and the message is size-limited, because it comes from the network.
    /// </summary>
    public static IReadOnlyList<WsMatch> ParseMatches(ReadOnlySpan<byte> message)
    {
        if (message.Length is 0 or > MaxMessage)
        {
            return [];
        }

        try
        {
            using var stream = new MemoryStream(message.ToArray());
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxMessage });
            var matches = new List<WsMatch>();
            foreach (var match in XDocument.Load(reader).Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
            {
                var types = match.Elements().FirstOrDefault(e => e.Name.LocalName == "Types")?.Value ?? string.Empty;
                var addresses = match.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value ?? string.Empty;
                if (!types.Contains("Computer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var address in addresses.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !uri.Host.Contains(':', StringComparison.Ordinal) && uri.Host.Length > 0)
                    {
                        matches.Add(new WsMatch(uri.Host));
                        break;
                    }
                }
            }

            return matches;
        }
        catch (XmlException)
        {
            return [];
        }
    }
}

/// <summary>
/// Finds computers by WS-Discovery (spec §7.1): the way Windows' own Network folder lists PCs and NAS boxes that run a WS-Discovery
/// responder (Windows, Samba's wsdd, Synology). Answers carry only an address, so the name comes from a reverse DNS lookup that is
/// given 1.5 s; without one the address is the name.
/// </summary>
public sealed class WsDiscovery : IDeviceDiscovery
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 3702);
    private static readonly TimeSpan NameTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly IMulticastTransport _transport;
    private readonly Func<string, CancellationToken, Task<string?>> _nameOf;

    /// <summary>Creates the discovery. <paramref name="window"/> defaults to 4 s; <paramref name="nameOf"/> (tests) replaces the reverse lookup.</summary>
    public WsDiscovery(IMulticastTransport transport, TimeSpan? window = null, Func<string, CancellationToken, Task<string?>>? nameOf = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        Window = window ?? TimeSpan.FromSeconds(4);
        _nameOf = nameOf ?? ReverseLookupAsync;
    }

    /// <summary>How long to listen for answers.</summary>
    public TimeSpan Window { get; }

    /// <inheritdoc />
    public string Name => "ws-discovery";

    /// <inheritdoc />
    public async Task DiscoverAsync(Action<DiscoveredDevice> found, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(found);
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var lookups = new System.Collections.Concurrent.ConcurrentBag<Task>();
        await _transport.ExchangeAsync(
            Group,
            WsDiscoveryMessage.BuildProbe(Guid.NewGuid()),
            Window,
            (packet, _) =>
            {
                foreach (var match in WsDiscoveryMessage.ParseMatches(packet))
                {
                    if (seen.TryAdd(match.Host, 0))
                    {
                        lookups.Add(ReportAsync(match.Host, found, cancellationToken));
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(lookups).ConfigureAwait(false);
    }

    private async Task ReportAsync(string host, Action<DiscoveredDevice> found, CancellationToken cancellationToken)
    {
        string? name = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(NameTimeout);
            name = await _nameOf(host, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No name in time; the address will do.
        }

        cancellationToken.ThrowIfCancellationRequested();
        found(new DiscoveredDevice(host, string.IsNullOrWhiteSpace(name) ? null : name));
    }

    private static async Task<string?> ReverseLookupAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(host, cancellationToken).ConfigureAwait(false);
            var first = entry.HostName.Split('.')[0];
            return first.Length > 0 && first != host ? first : null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}
