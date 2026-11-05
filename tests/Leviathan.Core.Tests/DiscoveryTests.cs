using System.Net;
using System.Text;
using Leviathan.Core.Discovery;

namespace Leviathan.Core.Tests;

/// <summary>Finding devices for Network ▸ Nearby (spec §7.1): mDNS <c>_smb._tcp</c> and WS-Discovery, from canned packets.</summary>
public sealed class DiscoveryTests
{
    // A DNS writer just big enough to build mDNS responses, so the tests read like the packets they feed in.
    private sealed class Wire
    {
        private readonly List<byte> _bytes = [];

        public int Position => _bytes.Count;

        public Wire U16(int value)
        {
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
            return this;
        }

        public Wire Name(string name)
        {
            foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                _bytes.Add((byte)label.Length);
                _bytes.AddRange(Encoding.UTF8.GetBytes(label));
            }

            _bytes.Add(0);
            return this;
        }

        public Wire Raw(params byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public Wire Header(int flags, int questions, int answers, int authority, int additional) =>
            U16(0).U16(flags).U16(questions).U16(answers).U16(authority).U16(additional);

        // name, type, class IN, ttl 120, rdata (length-prefixed)
        public Wire Record(string name, int type, Wire data) => Name(name).U16(type).U16(1).Raw(0, 0, 0, 120).U16(data._bytes.Count).Raw([.. data._bytes]);

        public byte[] ToArray() => [.. _bytes];
    }

    private static byte[] DiskStationResponse(bool withAddress = true, bool withService = true)
    {
        var wire = new Wire().Header(0x8400, 0, 1, 0, (withService ? 1 : 0) + (withAddress ? 1 : 0));
        wire.Record("_smb._tcp.local", 12, new Wire().Name("DiskStation._smb._tcp.local"));
        if (withService)
        {
            wire.Record("DiskStation._smb._tcp.local", 33, new Wire().U16(0).U16(0).U16(445).Name("diskstation.local"));
        }

        if (withAddress)
        {
            wire.Record("diskstation.local", 1, new Wire().Raw(192, 168, 1, 10));
        }

        return wire.ToArray();
    }

    [Fact]
    public void Parse_ReadsPointerServiceAndAddressRecords()
    {
        var records = DnsPacket.Parse(DiskStationResponse());

        Assert.Equal([DnsRecordType.Ptr, DnsRecordType.Srv, DnsRecordType.A], records.Select(r => r.Type));
        Assert.Equal("DiskStation._smb._tcp.local", records[0].Target);
        Assert.Equal("diskstation.local", records[1].Target);
        Assert.Equal(445, records[1].Port);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), records[2].Address);
    }

    [Fact]
    public void Devices_UseTheAddressAsHost_AndTheInstanceAsName()
    {
        var device = Assert.Single(SmbAdvertisement.Devices(DnsPacket.Parse(DiskStationResponse())));

        Assert.Equal("192.168.1.10", device.Host);
        Assert.Equal("DiskStation", device.Name);
    }

    [Fact]
    public void Devices_WithoutAnAddress_UseTheAnnouncedHostName()
    {
        var device = Assert.Single(SmbAdvertisement.Devices(DnsPacket.Parse(DiskStationResponse(withAddress: false))));

        Assert.Equal("diskstation.local", device.Host);
    }

    [Fact]
    public void Devices_WithoutAServiceRecord_AreSkipped()
    {
        Assert.Empty(SmbAdvertisement.Devices(DnsPacket.Parse(DiskStationResponse(withService: false))));
    }

    [Fact]
    public void Parse_FollowsCompressionPointers()
    {
        // The instance name in the PTR data is "DiskStation" + a pointer to the "_smb._tcp.local" at offset 12.
        var wire = new Wire().Header(0x8400, 0, 1, 0, 0);
        Assert.Equal(12, wire.Position);
        wire.Name("_smb._tcp.local").U16(12).U16(1).Raw(0, 0, 0, 120).U16(14).Raw(11).Raw([.. Encoding.ASCII.GetBytes("DiskStation")]).Raw(0xC0, 12);

        var record = Assert.Single(DnsPacket.Parse(wire.ToArray()));

        Assert.Equal("DiskStation._smb._tcp.local", record.Target);
    }

    [Fact]
    public void Parse_IgnoresQueriesAndShortPackets()
    {
        var query = DnsPacket.BuildQuery(SmbAdvertisement.Service, DnsRecordType.Ptr);

        Assert.Empty(DnsPacket.Parse(query));
        Assert.Empty(DnsPacket.Parse([]));
        Assert.Empty(DnsPacket.Parse(new byte[5]));
    }

    [Fact]
    public void Parse_OfAPointerLoop_GivesUpInsteadOfSpinning()
    {
        // The name at offset 12 is a pointer to itself.
        var wire = new Wire().Header(0x8400, 0, 1, 0, 0).Raw(0xC0, 12).U16(12).U16(1).Raw(0, 0, 0, 1).U16(0);

        Assert.Empty(DnsPacket.Parse(wire.ToArray()));
    }

    [Fact]
    public void Parse_OfAnyTruncation_NeverThrows()
    {
        var packet = DiskStationResponse();

        for (var length = 0; length <= packet.Length; length++)
        {
            var records = DnsPacket.Parse(packet.AsSpan(0, length));

            Assert.True(records.Count <= 3);
        }
    }

    [Fact]
    public void Parse_OfRandomBytes_NeverThrows()
    {
        var random = new Random(12345);
        var bytes = new byte[300];
        for (var round = 0; round < 500; round++)
        {
            random.NextBytes(bytes);
            bytes[2] |= 0x80; // looks like a response, so the body is read

            _ = DnsPacket.Parse(bytes);
        }
    }

    [Fact]
    public void BuildQuery_AsksForTheNameAndType()
    {
        var query = DnsPacket.BuildQuery("_smb._tcp.local", DnsRecordType.Ptr);

        Assert.Equal(1, query[5]);
        Assert.Equal(new Wire().Name("_smb._tcp.local").U16(12).U16(1).ToArray(), query[12..]);
    }

    [Fact]
    public void BuildQuery_RefusesAnInvalidName()
    {
        Assert.Throws<ArgumentException>(() => DnsPacket.BuildQuery("a..b", DnsRecordType.Ptr));
        Assert.Throws<ArgumentException>(() => DnsPacket.BuildQuery(new string('x', 64) + ".local", DnsRecordType.Ptr));
    }

    // WS-Discovery -------------------------------------------------------------------------------------------------------

    private static byte[] ProbeMatches(params (string Types, string XAddrs)[] matches) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
          <s:Body>
            <d:ProbeMatches>
              {string.Concat(matches.Select(m => $"<d:ProbeMatch><d:Types>{m.Types}</d:Types><d:XAddrs>{m.XAddrs}</d:XAddrs></d:ProbeMatch>"))}
            </d:ProbeMatches>
          </s:Body>
        </s:Envelope>
        """);

    [Fact]
    public void Probe_IsWellFormed_AndAsksForComputers()
    {
        var text = Encoding.UTF8.GetString(WsDiscoveryMessage.BuildProbe(Guid.Parse("11111111-2222-3333-4444-555555555555")));

        var document = System.Xml.Linq.XDocument.Parse(text);
        Assert.Contains("urn:uuid:11111111-2222-3333-4444-555555555555", text);
        Assert.Contains("Probe", text);
        Assert.Equal("pub:Computer", document.Descendants().Single(e => e.Name.LocalName == "Types").Value);
    }

    [Fact]
    public void ParseMatches_GivesTheHostOfEachComputer()
    {
        var message = ProbeMatches(
            ("wsdp:Device pub:Computer", "http://192.168.1.10:5357/uuid-1 http://[fe80::1]:5357/uuid-1"),
            ("wsdp:Device pub:Computer", "http://[fe80::2]:5357/uuid-2"),
            ("wsdp:Device wprt:PrintDeviceType", "http://192.168.1.50:5357/uuid-3"),
            ("pub:Computer", "http://pi.local:5357/uuid-4"));

        var matches = WsDiscoveryMessage.ParseMatches(message);

        Assert.Equal(["192.168.1.10", "pi.local"], matches.Select(m => m.Host));
    }

    [Fact]
    public void ParseMatches_RefusesDtdsGarbageAndHugeMessages()
    {
        var withDtd = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x>&e;</x>");

        Assert.Empty(WsDiscoveryMessage.ParseMatches(withDtd));
        Assert.Empty(WsDiscoveryMessage.ParseMatches("not xml at all"u8));
        Assert.Empty(WsDiscoveryMessage.ParseMatches([]));
        Assert.Empty(WsDiscoveryMessage.ParseMatches(new byte[100_000]));
    }

    // The discoveries over a canned transport ---------------------------------------------------------------------------

    private sealed class CannedTransport : IMulticastTransport
    {
        public List<byte[]> Answers { get; } = [];

        public IPEndPoint? Group { get; private set; }

        public byte[]? Sent { get; private set; }

        public TimeSpan? Window { get; private set; }

        public Task ExchangeAsync(IPEndPoint group, byte[] payload, TimeSpan window, Action<byte[], IPAddress> onPacket, CancellationToken cancellationToken)
        {
            Group = group;
            Sent = payload;
            Window = window;
            foreach (var answer in Answers)
            {
                onPacket(answer, IPAddress.Parse("192.168.1.10"));
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Mdns_AsksForSmbOnTheMdnsGroup_AndReportsWhoAnswers()
    {
        var transport = new CannedTransport { Answers = { DiskStationResponse(), new byte[] { 1, 2, 3 } } };
        var found = new List<DiscoveredDevice>();

        await new MdnsDiscovery(transport, TimeSpan.FromSeconds(2)).DiscoverAsync(found.Add, CancellationToken.None);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), transport.Group);
        Assert.Equal(DnsPacket.BuildQuery(SmbAdvertisement.Service, DnsRecordType.Ptr), transport.Sent);
        Assert.Equal(TimeSpan.FromSeconds(2), transport.Window);
        Assert.Equal([new DiscoveredDevice("192.168.1.10", "DiskStation")], found);
    }

    [Fact]
    public async Task WsDiscovery_ProbesTheWsGroup_NamesComputersAndReportsEachOnce()
    {
        var answer = ProbeMatches(("pub:Computer", "http://192.168.1.20:5357/a"), ("pub:Computer", "http://192.168.1.21:5357/b"));
        var transport = new CannedTransport { Answers = { answer, answer } };
        var found = new List<DiscoveredDevice>();

        await new WsDiscovery(transport, nameOf: (host, _) => Task.FromResult<string?>(host.EndsWith("20", StringComparison.Ordinal) ? "QNAP" : null))
            .DiscoverAsync(device => { lock (found) { found.Add(device); } }, CancellationToken.None);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702), transport.Group);
        Assert.Contains("Probe", Encoding.UTF8.GetString(transport.Sent!));
        Assert.Equal(
            [new DiscoveredDevice("192.168.1.20", "QNAP"), new DiscoveredDevice("192.168.1.21", null)],
            found.OrderBy(d => d.Host).ToList());
    }

    [Fact]
    public async Task WsDiscovery_WhenTheNameLookupHangs_StillReportsTheAddress()
    {
        var transport = new CannedTransport { Answers = { ProbeMatches(("pub:Computer", "http://192.168.1.20:5357/a")) } };
        var found = new List<DiscoveredDevice>();

        await new WsDiscovery(transport, nameOf: async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        }).DiscoverAsync(found.Add, CancellationToken.None);

        Assert.Equal([new DiscoveredDevice("192.168.1.20", null)], found);
    }

    // The real sockets, with a short window and on this machine only ----------------------------------------------------

    [Fact]
    public async Task UdpTransport_EndsWhenTheWindowEnds()
    {
        var transport = new UdpMulticastTransport();
        Assert.SkipWhen(UdpMulticastTransport.LocalAddresses().Count == 0, "No network adapter that can multicast.");

        var started = DateTime.UtcNow;
        await transport.ExchangeAsync(new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), DnsPacket.BuildQuery(SmbAdvertisement.Service, DnsRecordType.Ptr), TimeSpan.FromMilliseconds(300), (_, _) => { }, CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UdpTransport_StopsWhenTheCallerCancels()
    {
        var transport = new UdpMulticastTransport();
        Assert.SkipWhen(UdpMulticastTransport.LocalAddresses().Count == 0, "No network adapter that can multicast.");
        using var cancel = new CancellationTokenSource();
        var exchange = transport.ExchangeAsync(new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), [0], TimeSpan.FromSeconds(30), (_, _) => { }, cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
