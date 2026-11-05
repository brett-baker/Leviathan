using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Sidebar;

/// <summary>A server the user connected to, kept under Network in the tree (spec §7.1 "Saved servers").</summary>
/// <param name="Host">The name or address the user knows it by, such as <c>diskstation</c> or <c>192.168.1.10</c>.</param>
/// <param name="Name">The name shown in the tree, or null to show the host.</param>
public sealed record SavedConnection(string Host, string? Name = null)
{
    /// <summary>What the tree shows.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    /// <summary>The server's root, whose children are its shares.</summary>
    public LocationPath Location => LocationPath.Parse(@"\\" + Host);
}

/// <summary>A device found on the network by discovery (spec §7.1 "Nearby").</summary>
/// <param name="Host">Name or address to connect to.</param>
/// <param name="Name">The name the device announces (a NAS's friendly name), or null to show the host.</param>
public sealed record NearbyDevice(string Host, string? Name = null)
{
    /// <summary>What the tree shows.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Host : Name;
}

/// <summary>Where saved servers are kept between runs. Writes must be atomic (spec §17). Holds no passwords: Windows keeps those (spec §3.3).</summary>
public interface ISavedConnectionsStorage
{
    /// <summary>Reads the saved servers; a missing or damaged file gives an empty list.</summary>
    IReadOnlyList<SavedConnection> Load();

    /// <summary>Saves the servers.</summary>
    void Save(IReadOnlyList<SavedConnection> connections);
}

/// <summary>
/// The servers under Network (spec §7.1). Add connection puts one here, and so does opening a share on a server for the first time, so
/// "servers you have connected to" stays true without a separate step; Remove takes it off again. Used from the UI thread.
/// </summary>
public sealed class SavedConnectionList
{
    private readonly ISavedConnectionsStorage _storage;
    private readonly List<SavedConnection> _items;

    /// <summary>Creates the list from what <paramref name="storage"/> holds.</summary>
    public SavedConnectionList(ISavedConnectionsStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _items = [.. storage.Load().Where(c => IsValidHost(c.Host)).DistinctBy(c => Normalize(c.Host), StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Raised after the list changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The saved servers in the order they were added.</summary>
    public IReadOnlyList<SavedConnection> Items => _items;

    /// <summary>True when <paramref name="host"/> is saved (names compare without case, and a leading <c>\\</c> is ignored).</summary>
    public bool Contains(string host) => IndexOf(host) >= 0;

    /// <summary>
    /// Saves the server unless it is already saved. Returns true when something was added. A host that is not a plain server name or
    /// address (an empty string, a path, a name with spaces or slashes) is refused.
    /// </summary>
    public bool Add(string host, string? name = null)
    {
        if (!IsValidHost(host) || Contains(host))
        {
            return false;
        }

        _items.Add(new SavedConnection(Normalize(host), string.IsNullOrWhiteSpace(name) ? null : name.Trim()));
        Persist();
        return true;
    }

    /// <summary>Takes the server off the list. Returns true when it was there.</summary>
    public bool Remove(string host)
    {
        var index = IndexOf(host);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        Persist();
        return true;
    }

    /// <summary>Gives a server its own name, or null to show the host again.</summary>
    public void Rename(string host, string? name)
    {
        var index = IndexOf(host);
        if (index >= 0)
        {
            _items[index] = _items[index] with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() };
            Persist();
        }
    }

    /// <summary>Makes a host name comparable: no leading <c>\\</c>, no trailing separators, no surrounding blanks.</summary>
    public static string Normalize(string host) => (host ?? string.Empty).Trim().TrimStart('\\', '/').TrimEnd('\\', '/');

    /// <summary>True for a server name or address: not empty, no separators, blanks or characters a host name cannot have.</summary>
    public static bool IsValidHost(string host)
    {
        var name = Normalize(host);
        return name.Length is > 0 and <= 255 && name.AsSpan().IndexOfAny(BadHostCharacters) < 0;
    }

    private static readonly System.Buffers.SearchValues<char> BadHostCharacters = System.Buffers.SearchValues.Create(" \t\\/:*?\"<>|");

    private int IndexOf(string host)
    {
        var name = Normalize(host);
        return _items.FindIndex(c => string.Equals(c.Host, name, StringComparison.OrdinalIgnoreCase));
    }

    private void Persist()
    {
        try
        {
            _storage.Save([.. _items]);
        }
        catch (IOException)
        {
            // Still listed for this run; just not remembered.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
