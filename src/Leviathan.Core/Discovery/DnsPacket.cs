using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Leviathan.Core.Discovery;

/// <summary>The record types mDNS discovery cares about.</summary>
public enum DnsRecordType : ushort
{
    /// <summary>IPv4 address.</summary>
    A = 1,

    /// <summary>Pointer: service name to instance name.</summary>
    Ptr = 12,

    /// <summary>Service location: instance to host and port.</summary>
    Srv = 33,

    /// <summary>IPv6 address.</summary>
    Aaaa = 28,
}

/// <summary>One record of a DNS message, reduced to what discovery reads.</summary>
/// <param name="Name">Owner name, e.g. <c>_smb._tcp.local</c>.</param>
/// <param name="Type">Record type.</param>
/// <param name="Target">For PTR the instance name, for SRV the host name; else null.</param>
/// <param name="Port">For SRV the port; else 0.</param>
/// <param name="Address">For A and AAAA the address; else null.</param>
public sealed record DnsRecord(string Name, DnsRecordType Type, string? Target = null, int Port = 0, IPAddress? Address = null);

/// <summary>
/// The little of DNS that multicast DNS service discovery needs (RFC 6762, RFC 6763): one question out, records in. Packets come from
/// the network and may be hostile or damaged, so every read is bounds-checked, compression pointers are followed a limited number of
/// times, and a bad packet gives fewer records, never an exception.
/// </summary>
public static class DnsPacket
{
    private const int HeaderLength = 12;
    private const int MaxLabels = 127;
    private const int MaxJumps = 16;
    private const int MaxName = 255;
    private const ushort ResponseFlag = 0x8000;
    private const ushort ClassIn = 1;

    /// <summary>A query for the records of type <paramref name="type"/> named <paramref name="name"/>, e.g. <c>_smb._tcp.local</c> / PTR.</summary>
    public static byte[] BuildQuery(string name, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var bytes = new List<byte>(HeaderLength + name.Length + 6);
        bytes.AddRange(new byte[HeaderLength]);
        bytes[5] = 1; // one question
        foreach (var label in name.Trim('.').Split('.'))
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            if (encoded.Length is 0 or > 63)
            {
                throw new ArgumentException($"'{name}' is not a valid DNS name.", nameof(name));
            }

            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.Add(0);
        bytes.Add((byte)((ushort)type >> 8));
        bytes.Add((byte)(ushort)type);
        bytes.Add(0);
        bytes.Add((byte)ClassIn);
        return [.. bytes];
    }

    /// <summary>Every PTR, SRV, A and AAAA record in the answer, authority and additional sections of a response; empty for anything else.</summary>
    public static IReadOnlyList<DnsRecord> Parse(ReadOnlySpan<byte> packet)
    {
        var records = new List<DnsRecord>();
        if (packet.Length < HeaderLength || (BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) & ResponseFlag) == 0)
        {
            return records;
        }

        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) + BinaryPrimitives.ReadUInt16BigEndian(packet[8..]) + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        var pos = HeaderLength;
        for (var i = 0; i < questions; i++)
        {
            if (ReadName(packet, ref pos) is null || pos + 4 > packet.Length)
            {
                return records;
            }

            pos += 4;
        }

        for (var i = 0; i < count; i++)
        {
            var name = ReadName(packet, ref pos);
            if (name is null || pos + 10 > packet.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[pos..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(pos + 8)..]);
            pos += 10;
            if (pos + length > packet.Length)
            {
                break;
            }

            var data = pos;
            pos += length;
            switch ((DnsRecordType)type)
            {
                case DnsRecordType.Ptr when ReadName(packet, ref data) is { } target:
                    records.Add(new DnsRecord(name, DnsRecordType.Ptr, target));
                    break;
                case DnsRecordType.Srv when length >= 7:
                    var port = BinaryPrimitives.ReadUInt16BigEndian(packet[(data + 4)..]);
                    var hostAt = data + 6;
                    if (ReadName(packet, ref hostAt) is { } host)
                    {
                        records.Add(new DnsRecord(name, DnsRecordType.Srv, host, port));
                    }

                    break;
                case DnsRecordType.A when length == 4:
                    records.Add(new DnsRecord(name, DnsRecordType.A, Address: new IPAddress(packet.Slice(data, 4))));
                    break;
                case DnsRecordType.Aaaa when length == 16:
                    records.Add(new DnsRecord(name, DnsRecordType.Aaaa, Address: new IPAddress(packet.Slice(data, 16))));
                    break;
            }
        }

        return records;
    }

    // A name is labels (length byte + text) ending in a zero byte, or a pointer (two bytes, top bits 11) to a name earlier in the
    // packet. Returns null for anything malformed; advances pos past the name as it appears at pos (not past a pointer's target).
    private static string? ReadName(ReadOnlySpan<byte> packet, ref int pos)
    {
        var builder = new StringBuilder();
        var cursor = pos;
        var jumps = 0;
        var end = -1;
        for (var labels = 0; labels <= MaxLabels; labels++)
        {
            if (cursor >= packet.Length)
            {
                return null;
            }

            int length = packet[cursor];
            if (length == 0)
            {
                pos = end >= 0 ? end : cursor + 1;
                return builder.ToString();
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length || ++jumps > MaxJumps)
                {
                    return null;
                }

                end = end >= 0 ? end : cursor + 2;
                cursor = ((length & 0x3F) << 8) | packet[cursor + 1];
                continue;
            }

            if ((length & 0xC0) != 0 || cursor + 1 + length > packet.Length)
            {
                return null;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.UTF8.GetString(packet.Slice(cursor + 1, length)));
            if (builder.Length > MaxName)
            {
                return null;
            }

            cursor += 1 + length;
        }

        return null;
    }
}

/// <summary>Turns the records of an <c>_smb._tcp</c> mDNS response into devices (spec §7.1; Synology and QNAP advertise this).</summary>
public static class SmbAdvertisement
{
    /// <summary>The service name asked for.</summary>
    public const string Service = "_smb._tcp.local";

    /// <summary>
    /// One device for each instance the PTR records name. Its host is its IPv4 address when the response carried one (an address works
    /// without name resolution), else the SRV target (<c>nas.local</c>); an instance with neither is skipped. The instance label is the name.
    /// </summary>
    public static IReadOnlyList<DiscoveredDevice> Devices(IReadOnlyList<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var srv = records.Where(r => r.Type == DnsRecordType.Srv).ToLookup(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var addresses = records
            .Where(r => r.Type == DnsRecordType.A && r.Address is not null)
            .ToLookup(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var devices = new List<DiscoveredDevice>();
        foreach (var pointer in records.Where(r => r.Type == DnsRecordType.Ptr && r.Target is not null && r.Name.Equals(Service, StringComparison.OrdinalIgnoreCase)))
        {
            var instance = pointer.Target!;
            var host = srv[instance].FirstOrDefault()?.Target;
            if (host is null)
            {
                continue;
            }

            var address = addresses[host].FirstOrDefault()?.Address;
            var label = instance.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase) ? instance[..^(Service.Length + 1)] : instance;
            devices.Add(new DiscoveredDevice(address?.ToString() ?? host, string.IsNullOrWhiteSpace(label) ? null : label));
        }

        return devices;
    }
}
