using System.Globalization;
using Leviathan.Core.Paths;
using Leviathan.Providers.Remote;

namespace Leviathan.ViewModels.Sidebar;

/// <summary>
/// A server reached over SFTP, FTP or FTPS that the user added (spec §7.1 "Add connection"), kept under Network in the tree. The password is not part of it:
/// Windows Credential Manager keeps that, found by the endpoint (<see cref="Endpoint"/>), so the saved file holds nothing secret.
/// </summary>
/// <param name="Protocol">How to talk to it.</param>
/// <param name="Host">The host name or address.</param>
/// <param name="Port">The port, or null for the protocol's usual one.</param>
/// <param name="User">The account name, or null for an anonymous FTP login.</param>
/// <param name="KeyFile">For SFTP: a private key file to use instead of a password.</param>
/// <param name="Path">The folder to open first, with <c>/</c>; <c>/</c> for the top.</param>
/// <param name="Name">The name shown in the tree, or null to show the user and host.</param>
public sealed record RemoteConnection(RemoteProtocol Protocol, string Host, int? Port = null, string? User = null, string? KeyFile = null, string Path = "/", string? Name = null)
{
    /// <summary>What the tree shows.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? (User is null ? Host : $"{User}@{Host}") : Name;

    /// <summary>The server and account, for finding the saved password and the trusted key.</summary>
    public RemoteEndpoint Endpoint => new(Protocol, Host, Port ?? RemoteEndpoint.DefaultPort(Protocol), User, KeyFile);

    /// <summary>The folder it opens, as a location (<c>sftp://user@host:port/path</c>).</summary>
    public LocationPath Location
    {
        get
        {
            var scheme = Protocol.ToString().ToLowerInvariant();
            var user = string.IsNullOrEmpty(User) ? string.Empty : Uri.EscapeDataString(User) + "@";
            var port = Port is { } p && p != RemoteEndpoint.DefaultPort(Protocol) ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var path = string.Join('/', Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            return LocationPath.Parse($"{scheme}://{user}{Host}{port}/{path}");
        }
    }

    /// <summary>The same connection with another name.</summary>
    public string Key => Endpoint.Key;
}

/// <summary>Where the remote connections are kept between runs. Writes must be atomic (spec §17). Holds no passwords.</summary>
public interface IRemoteConnectionsStorage
{
    /// <summary>Reads the connections; a missing or damaged file gives none.</summary>
    IReadOnlyList<RemoteConnection> Load();

    /// <summary>Saves the connections.</summary>
    void Save(IReadOnlyList<RemoteConnection> connections);
}

/// <summary>What the Add connection dialog collected for a server that is not a Windows share.</summary>
/// <param name="Protocol">The protocol chosen.</param>
/// <param name="Host">The host as typed.</param>
/// <param name="Port">The port as typed, or empty.</param>
/// <param name="User">The user as typed.</param>
/// <param name="Password">The password as typed (kept in Credential Manager, never in the saved file).</param>
/// <param name="KeyFile">The key file as typed, or empty.</param>
/// <param name="Path">The folder as typed, or empty.</param>
/// <param name="Name">The name for the tree, or empty.</param>
public sealed record RemoteConnectionInput(RemoteProtocol Protocol, string Host, string Port, string User, string Password, string KeyFile, string Path, string Name)
{
    private static readonly System.Buffers.SearchValues<char> BadHostCharacters = NfsSupport.BadHostCharacters;

    /// <summary>Checks the input and makes the connection, or says what is wrong in words for the user.</summary>
    public RemoteConnection? ToConnection(out string problem)
    {
        problem = string.Empty;
        var host = Host.Trim();
        if (host.StartsWith(Protocol.ToString() + "://", StringComparison.OrdinalIgnoreCase))
        {
            host = host[(Protocol.ToString().Length + 3)..];
        }

        host = host.Trim('/');
        if (host.Length == 0 || host.Length > 255 || host.AsSpan().IndexOfAny(NfsSupport.BadHostCharacters) >= 0)
        {
            problem = "Enter the server's name or address, such as nas.local or 192.168.1.10.";
            return null;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(Port))
        {
            if (!int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535)
            {
                problem = "The port is a number from 1 to 65535.";
                return null;
            }

            port = parsed == RemoteEndpoint.DefaultPort(Protocol) ? null : parsed;
        }

        var user = string.IsNullOrWhiteSpace(User) ? null : User.Trim();
        if (user is null && Protocol == RemoteProtocol.Sftp)
        {
            problem = "SFTP needs a user name.";
            return null;
        }

        var key = string.IsNullOrWhiteSpace(KeyFile) ? null : KeyFile.Trim().Trim('"');
        if (key is not null && Protocol != RemoteProtocol.Sftp)
        {
            problem = "A key file is only for SFTP.";
            return null;
        }

        var path = string.IsNullOrWhiteSpace(Path) ? "/" : Path.Trim().Replace('\\', '/');
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        if (path.Contains("..", StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            problem = "The folder must be a plain path such as /home/brett.";
            return null;
        }

        return new RemoteConnection(Protocol, host.ToLowerInvariant(), port, user, key, path, string.IsNullOrWhiteSpace(Name) ? null : Name.Trim());
    }
}

/// <summary>
/// The SFTP, FTP and FTPS servers under Network (spec §7.1). Used from the UI thread. Adding a connection with the same server and account replaces
/// the earlier one (so a changed folder or name takes effect), and removing one is up to the caller to follow with forgetting its password.
/// </summary>
public sealed class RemoteConnectionList
{
    private readonly IRemoteConnectionsStorage _storage;
    private readonly List<RemoteConnection> _items;

    /// <summary>Loads the saved connections.</summary>
    public RemoteConnectionList(IRemoteConnectionsStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _items = [.. storage.Load().DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Raised after the list changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The connections, in the order they were added.</summary>
    public IReadOnlyList<RemoteConnection> Items => _items;

    /// <summary>Adds a connection, or replaces the one for the same server and account.</summary>
    public void AddOrReplace(RemoteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var at = _items.FindIndex(c => string.Equals(c.Key, connection.Key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            _items[at] = connection;
        }
        else
        {
            _items.Add(connection);
        }

        Persist();
    }

    /// <summary>Removes the connection that opens <paramref name="location"/>'s server, returning it (so the caller can forget its password), or null.</summary>
    public RemoteConnection? Remove(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var at = _items.FindIndex(c => SameServer(c, location));
        if (at < 0)
        {
            return null;
        }

        var removed = _items[at];
        _items.RemoveAt(at);
        Persist();
        return removed;
    }

    /// <summary>The connection a location belongs to, or null (for finding its name or password).</summary>
    public RemoteConnection? Find(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return _items.Find(c => SameServer(c, location));
    }

    private static bool SameServer(RemoteConnection connection, LocationPath location)
    {
        var location2 = RemoteLocationProvider.EndpointOf(location);
        return location.Kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps && string.Equals(connection.Endpoint.Key, location2.Key, StringComparison.OrdinalIgnoreCase);
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

/// <summary>
/// Whether Windows can open NFS exports (spec §7.1): through Windows' own Client for NFS, which makes <c>\\server\export</c> an ordinary network path. When the
/// feature is off the user is told how to turn it on instead of getting a path error.
/// </summary>
public static class NfsSupport
{
    internal static readonly System.Buffers.SearchValues<char> BadHostCharacters = System.Buffers.SearchValues.Create(" \t\\/:@*?\"<>|");

    /// <summary>The program that shows Client for NFS is installed.</summary>
    public static string ClientProgram(string systemDirectory) => System.IO.Path.Combine(systemDirectory, "mount.exe");

    /// <summary>True when Client for NFS is installed.</summary>
    public static bool IsInstalled(System.IO.Abstractions.IFileSystem files, string systemDirectory)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.File.Exists(ClientProgram(systemDirectory));
    }

    /// <summary>The network path of an export on a server, as Windows' NFS client takes it (<c>\\server\export\folder</c>).</summary>
    public static LocationPath? PathOf(string host, string export, out string problem)
    {
        problem = string.Empty;
        var server = (host ?? string.Empty).Trim().Trim('\\', '/');
        var path = (export ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (server.Length == 0 || server.AsSpan().IndexOfAny(BadHostCharacters) >= 0)
        {
            problem = "Enter the server's name or address.";
            return null;
        }

        if (path.Length == 0)
        {
            problem = "Enter the export on the server, such as /volume1/media.";
            return null;
        }

        return LocationPath.TryParse(@"\\" + server + @"\" + path.Replace('/', '\\'), out var location) ? location : Fail("That export could not be turned into a network path.", out problem);
    }

    private static LocationPath? Fail(string message, out string problem)
    {
        problem = message;
        return null;
    }

    /// <summary>What to tell someone whose Windows has no Client for NFS.</summary>
    public const string NotInstalledMessage = "Windows' Client for NFS is not installed. Open Turn Windows features on or off, tick Services for NFS > Client for NFS, restart, and add the connection again.";
}
