using System.Text.Json;
using Leviathan.Providers.Remote;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.App;

/// <summary>The SFTP, FTP and FTPS connections, in <c>remote-connections.json</c> (spec §7.1, §17: written atomically). No passwords: Credential Manager keeps those.</summary>
internal sealed class JsonRemoteConnectionsStorage : IRemoteConnectionsStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonRemoteConnectionsStorage(string? path = null) =>
        _path = path ?? Leviathan.Core.Platform.DataLocation.Combine("remote-connections.json");

    public IReadOnlyList<RemoteConnection> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_path)) ?? [];
            return [.. stored.Where(s => Enum.TryParse<RemoteProtocol>(s.Protocol, true, out _) && !string.IsNullOrWhiteSpace(s.Host))
                .Select(s => new RemoteConnection(Enum.Parse<RemoteProtocol>(s.Protocol, true), s.Host, s.Port, s.User, s.KeyFile, string.IsNullOrWhiteSpace(s.Path) ? "/" : s.Path, s.Name))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file starts the list afresh; the connections are quick to add again.
            return [];
        }
    }

    public void Save(IReadOnlyList<RemoteConnection> connections)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(connections.Select(c => new Stored(c.Protocol.ToString(), c.Host, c.Port, c.User, c.KeyFile, c.Path, c.Name)), Options));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record Stored(string Protocol, string Host, int? Port, string? User, string? KeyFile, string? Path, string? Name);
}

/// <summary>The SSH host keys and TLS certificates trusted so far, in <c>known-hosts.json</c> (spec §7.1: trust on first use, then a change is an error).</summary>
internal sealed class JsonKnownHosts : IKnownHosts
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string>? _items;

    public JsonKnownHosts(string? path = null) =>
        _path = path ?? Leviathan.Core.Platform.DataLocation.Combine("known-hosts.json");

    public string? Get(string endpointKey)
    {
        lock (_lock)
        {
            return Items().GetValueOrDefault(endpointKey.ToLowerInvariant());
        }
    }

    public void Set(string endpointKey, string fingerprint)
    {
        lock (_lock)
        {
            Items()[endpointKey.ToLowerInvariant()] = fingerprint;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Items(), Options));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private Dictionary<string, string> Items()
    {
        if (_items is not null)
        {
            return _items;
        }

        try
        {
            _items = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _items = [];
        }

        return _items;
    }
}
