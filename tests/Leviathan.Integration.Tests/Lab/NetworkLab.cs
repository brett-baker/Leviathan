using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Leviathan.Integration.Tests.Lab;

/// <summary>
/// The Samba + Toxiproxy test lab from <c>docker/compose.yml</c> (spec §19.2): Samba on localhost:1445 and Toxiproxy's API on
/// localhost:8474 with a proxy listening on 2445. Tests that need it call <see cref="Require"/>, which skips them with the reason
/// when the lab is not running, so a machine without Docker stays green. Nothing here ever points at a real share.
/// </summary>
public sealed class NetworkLab : IAsyncLifetime
{
    /// <summary>Samba, direct.</summary>
    public const int SambaPort = 1445;

    /// <summary>The Toxiproxy listener in front of Samba.</summary>
    public const int ProxyPort = 2445;

    private const string ProxyName = "smb";
    private static readonly Uri Api = new("http://127.0.0.1:8474/");
    private readonly HttpClient _http = new() { BaseAddress = Api, Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>True when both Samba and the Toxiproxy API answered.</summary>
    public bool Available { get; private set; }

    /// <summary>Why the lab is not available, for the skip message.</summary>
    public string Reason { get; private set; } = "not probed";

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!await CanConnectAsync(SambaPort))
        {
            Reason = $"the Samba container is not listening on localhost:{SambaPort} (docker compose -f docker/compose.yml up -d)";
            return;
        }

        try
        {
            using var version = await _http.GetAsync("version");
            await _http.DeleteAsync("proxies/" + ProxyName);
            var body = "{\"name\":\"" + ProxyName + "\",\"listen\":\"0.0.0.0:" + ProxyPort + "\",\"upstream\":\"samba:445\",\"enabled\":true}";
            (await _http.PostAsync("proxies", Json(body))).EnsureSuccessStatusCode();
            Available = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Reason = "the Toxiproxy API on localhost:8474 did not answer: " + ex.Message;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Available)
        {
            try
            {
                await _http.DeleteAsync("proxies/" + ProxyName);
            }
            catch (HttpRequestException)
            {
                // The lab went away first; nothing to clean.
            }
        }

        _http.Dispose();
    }

    /// <summary>Skips the calling test, with the reason, when the lab is not running.</summary>
    public void Require() => Assert.SkipUnless(Available, "Network lab unavailable: " + Reason);

    /// <summary>Removes every toxic and re-enables the proxy, so tests start from a clean line.</summary>
    public async Task ResetAsync() => (await _http.PostAsync("reset", null)).EnsureSuccessStatusCode();

    /// <summary>Adds latency (milliseconds) to traffic from Samba to the client.</summary>
    public Task AddLatencyAsync(int milliseconds) => AddToxicAsync("latency", "{\"latency\":" + milliseconds + "}");

    /// <summary>Caps bandwidth (KB/s) from Samba to the client.</summary>
    public Task LimitBandwidthAsync(int kilobytesPerSecond) => AddToxicAsync("bandwidth", "{\"rate\":" + kilobytesPerSecond + "}");

    /// <summary>Drops the line: the proxy stops accepting and closes open connections, like a pulled cable.</summary>
    public async Task CutAsync() => (await _http.PostAsync("proxies/" + ProxyName, Json("{\"enabled\":false}"))).EnsureSuccessStatusCode();

    /// <summary>Plugs the line back in.</summary>
    public async Task RestoreAsync() => (await _http.PostAsync("proxies/" + ProxyName, Json("{\"enabled\":true}"))).EnsureSuccessStatusCode();

    /// <summary>Sends an SMB2 NEGOTIATE to <paramref name="port"/> and returns true when an SMB2 reply comes back within <paramref name="timeout"/>.</summary>
    public static async Task<bool> SmbAnswersAsync(int port, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(NegotiateRequest(), cts.Token);
            var reply = new byte[8];
            var read = 0;
            while (read < reply.Length)
            {
                var n = await stream.ReadAsync(reply.AsMemory(read), cts.Token);
                if (n == 0)
                {
                    return false;
                }

                read += n;
            }

            // NetBIOS length (4 bytes), then 0xFE 'S' 'M' 'B'.
            return reply[4] == 0xFE && reply[5] == (byte)'S' && reply[6] == (byte)'M' && reply[7] == (byte)'B';
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] NegotiateRequest()
    {
        using var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(new byte[] { 0xFE, (byte)'S', (byte)'M', (byte)'B' });
        w.Write((ushort)64);      // header size
        w.Write((ushort)0);       // credit charge
        w.Write(0u);              // status
        w.Write((ushort)0);       // command: NEGOTIATE
        w.Write((ushort)1);       // credits requested
        w.Write(0u);              // flags
        w.Write(0u);              // next command
        w.Write(0UL);             // message id
        w.Write(0u);              // reserved
        w.Write(0u);              // tree id
        w.Write(0UL);             // session id
        w.Write(new byte[16]);    // signature
        w.Write((ushort)36);      // body size
        w.Write((ushort)1);       // dialect count
        w.Write((ushort)1);       // security mode
        w.Write((ushort)0);       // reserved
        w.Write(0u);              // capabilities
        w.Write(new byte[16]);    // client guid
        w.Write(0UL);             // client start time
        w.Write((ushort)0x0202);  // SMB 2.0.2
        var packet = body.ToArray();
        var framed = new byte[packet.Length + 4];
        framed[1] = (byte)(packet.Length >> 16);
        framed[2] = (byte)(packet.Length >> 8);
        framed[3] = (byte)packet.Length;
        packet.CopyTo(framed, 4);
        return framed;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task AddToxicAsync(string type, string attributes)
    {
        var body = "{\"name\":\"" + type + "-downstream\",\"type\":\"" + type + "\",\"stream\":\"downstream\",\"toxicity\":1.0,\"attributes\":" + attributes + "}";
        (await _http.PostAsync("proxies/" + ProxyName + "/toxics", Json(body))).EnsureSuccessStatusCode();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
