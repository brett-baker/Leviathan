using System.Net;
using System.Net.Sockets;

namespace Leviathan.Core.Resilience;

/// <summary>Turns a host name into the addresses it answers on. Behind an interface so identity logic is testable without DNS.</summary>
public interface IHostAddressResolver
{
    /// <summary>The addresses <paramref name="host"/> resolves to; empty when it does not resolve.</summary>
    Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Resolves names with the system resolver (DNS, NetBIOS fallback of the OS, hosts file).</summary>
public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return [.. addresses.Select(a => a.ToString())];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>
/// Knows which names and addresses are the same server (spec §3.5 item 5: the NAS answers on two IPs, its NetBIOS name and a DDNS
/// name, and is one device). Names that share a resolved address are merged into one identity, so the tree shows the server once,
/// caches, folder sizes and per-device limits are shared across names, and a copy between two names of one NAS can be server-side.
/// The identity key is the first name seen for the server (normalised), and stays stable as aliases join. Thread-safe.
/// </summary>
public sealed class ServerIdentityMap
{
    private readonly Lock _lock = new();
    private readonly IHostAddressResolver _resolver;
    private readonly Dictionary<string, string> _keyOf = new(StringComparer.Ordinal);

    /// <summary>Creates a map that resolves unknown names with <paramref name="resolver"/>.</summary>
    public ServerIdentityMap(IHostAddressResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <summary>Lower-cases a host, drops a trailing dot and IPv6 brackets or zone, so spellings of one name compare equal.</summary>
    public static string Normalize(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var text = host.Trim().TrimEnd('.').Trim('[', ']');
        var zone = text.IndexOf('%', StringComparison.Ordinal);
        if (zone > 0)
        {
            text = text[..zone];
        }

        return text.ToLowerInvariant();
    }

    /// <summary>The identity of <paramref name="host"/> as far as it is known now, without any lookup: the normalised name when it has not been identified.</summary>
    public string KeyFor(string host)
    {
        var name = Normalize(host);
        lock (_lock)
        {
            return _keyOf.TryGetValue(name, out var key) ? key : name;
        }
    }

    /// <summary>True when both names are known to be the same server.</summary>
    public bool AreSame(string a, string b) => string.Equals(KeyFor(a), KeyFor(b), StringComparison.Ordinal);

    /// <summary>
    /// Resolves <paramref name="host"/> and merges it with every known name that shares an address, then returns the identity key.
    /// A failed or slow lookup leaves the name on its own identity, never blocks longer than <paramref name="timeout"/> and never throws.
    /// </summary>
    public async Task<string> IdentifyAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var name = Normalize(host);
        IReadOnlyList<string> addresses = [];
        if (!IsAddress(name))
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                addresses = await _resolver.ResolveAsync(name, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out: stay on our own name.
            }
        }

        lock (_lock)
        {
            var members = new List<string> { name };
            members.AddRange(addresses.Select(Normalize).Where(a => !IsLoopbackOrLinkLocal(a)));
            var keys = members.Select(m => _keyOf.TryGetValue(m, out var k) ? k : null).Where(k => k is not null).Distinct().ToList();
            // The oldest identity wins: the key an existing alias already shows must not change under it.
            var key = keys.Count > 0 ? keys[0]! : name;
            foreach (var other in keys.Skip(1))
            {
                foreach (var entry in _keyOf.Where(e => e.Value == other).ToList())
                {
                    _keyOf[entry.Key] = key;
                }
            }

            foreach (var member in members)
            {
                _keyOf[member] = key;
            }

            return key;
        }
    }

    private static bool IsAddress(string name) => IPAddress.TryParse(name, out _);

    private static bool IsLoopbackOrLinkLocal(string address) =>
        IPAddress.TryParse(address, out var ip) && (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
}
