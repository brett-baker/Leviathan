using System.Text.Json;

namespace Leviathan.Providers.Remote;

/// <summary>
/// The SSH host keys and TLS certificates trusted so far, kept in <c>known-hosts.json</c> in the data folder (spec §7.1: trust on first use, then a change is an error).
/// The window and the worker both use the file: the window asks the user and writes, the worker only reads what the window saved. The file is read again whenever it
/// changed, so a server trusted in the window is known to a worker that was already running.
/// </summary>
public sealed class KnownHostsFile : IKnownHosts
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, string> _items = [];
    private DateTime _readAt = DateTime.MinValue;

    /// <summary>Uses <paramref name="path"/>, or <c>known-hosts.json</c> in the data folder.</summary>
    public KnownHostsFile(string? path = null) => _path = path ?? Leviathan.Core.Platform.DataLocation.Combine("known-hosts.json");

    /// <inheritdoc />
    public string? Get(string endpointKey)
    {
        ArgumentNullException.ThrowIfNull(endpointKey);
        lock (_lock)
        {
            Refresh();
            return _items.GetValueOrDefault(endpointKey.ToLowerInvariant());
        }
    }

    /// <inheritdoc />
    public void Set(string endpointKey, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(endpointKey);
        ArgumentNullException.ThrowIfNull(fingerprint);
        lock (_lock)
        {
            Refresh();
            _items[endpointKey.ToLowerInvariant()] = fingerprint;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, Options));
            File.Move(temp, _path, overwrite: true);
            _readAt = File.GetLastWriteTimeUtc(_path);
        }
    }

    private void Refresh()
    {
        try
        {
            var changed = File.GetLastWriteTimeUtc(_path);
            if (changed == _readAt)
            {
                return;
            }

            _items = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [] : [];
            _readAt = changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A file that cannot be read now is read again next time; what is known stays known.
        }
    }
}
