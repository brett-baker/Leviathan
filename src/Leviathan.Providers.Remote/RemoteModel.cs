namespace Leviathan.Providers.Remote;

/// <summary>The protocols this project speaks. NFS is not here: it goes through Windows' own NFS client as a network path (spec §7.1).</summary>
public enum RemoteProtocol
{
    /// <summary>SSH file transfer (<c>sftp://</c>).</summary>
    Sftp,

    /// <summary>Plain FTP (<c>ftp://</c>).</summary>
    Ftp,

    /// <summary>FTP over TLS (<c>ftps://</c>).</summary>
    Ftps,
}

/// <summary>Which server to talk to, and as whom (spec §7.1 "Add connection").</summary>
/// <param name="Protocol">How to talk to it.</param>
/// <param name="Host">The host name or address.</param>
/// <param name="Port">The port; 22 for SFTP and 21 for FTP and FTPS when the user gave none.</param>
/// <param name="User">The account name; null for an anonymous FTP login.</param>
/// <param name="KeyFile">For SFTP: a private key file to use instead of a password, or null.</param>
public sealed record RemoteEndpoint(RemoteProtocol Protocol, string Host, int Port, string? User, string? KeyFile = null)
{
    /// <summary>The default port of a protocol.</summary>
    public static int DefaultPort(RemoteProtocol protocol) => protocol == RemoteProtocol.Sftp ? 22 : 21;

    /// <summary>A short stable text that names the endpoint, for pools, secrets and known hosts. Not case sensitive for the host.</summary>
    public string Key => $"{Protocol.ToString().ToLowerInvariant()}://{User}@{Host.ToLowerInvariant()}:{Port}";
}

/// <summary>One file or folder on a server.</summary>
/// <param name="Name">The name.</param>
/// <param name="FullPath">The path on the server, with <c>/</c>.</param>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="Size">Size in bytes; 0 for a folder.</param>
/// <param name="Modified">Last change, UTC.</param>
/// <param name="IsLink">True for a symbolic link (not followed).</param>
public sealed record RemoteItem(string Name, string FullPath, bool IsFolder, long Size, DateTimeOffset Modified, bool IsLink = false);

/// <summary>
/// An open connection to one server (spec §7.1). Operations take server paths with <c>/</c>. A session is used by one operation at a time; the provider
/// serialises calls and replaces a session that failed.
/// </summary>
public interface IRemoteSession : IAsyncDisposable
{
    /// <summary>True while the connection is believed to be up.</summary>
    bool IsConnected { get; }

    /// <summary>Lists the items directly inside a folder (not <c>.</c> and <c>..</c>).</summary>
    Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>Describes one item, or null when there is none.</summary>
    Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken);

    /// <summary>Opens a file for reading.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes a stream to a file, replacing it. Callers write to a <c>*.leviathan-part</c> name and rename when complete (spec §3.4).</summary>
    Task WriteAsync(string path, Stream content, CancellationToken cancellationToken);

    /// <summary>Renames or moves an item on the same server.</summary>
    Task RenameAsync(string from, string to, CancellationToken cancellationToken);

    /// <summary>Deletes a file, or an empty folder.</summary>
    Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken);

    /// <summary>Creates a folder.</summary>
    Task CreateFolderAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Makes sessions. Behind an interface so the provider is tested without a server (spec §19).</summary>
public interface IRemoteSessionFactory
{
    /// <summary>Connects and signs in.</summary>
    /// <exception cref="RemoteException">The server could not be reached or refused the sign-in.</exception>
    Task<IRemoteSession> ConnectAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken);
}

/// <summary>Where passwords and key passphrases come from: Windows Credential Manager in the app (spec §7.1), a dictionary in tests. Never the saved connection file.</summary>
public interface IRemoteSecrets
{
    /// <summary>The password (or key passphrase) saved for the endpoint, or null.</summary>
    string? Get(RemoteEndpoint endpoint);

    /// <summary>Saves the password (or key passphrase) for the endpoint; null forgets it.</summary>
    void Set(RemoteEndpoint endpoint, string? secret);
}

/// <summary>The fingerprints of servers already trusted: SSH host keys and FTPS certificates (trust on first use, then a change is an error).</summary>
public interface IKnownHosts
{
    /// <summary>The fingerprint saved for a server, or null when it has not been seen.</summary>
    string? Get(string endpointKey);

    /// <summary>Saves the fingerprint of a server the user trusted.</summary>
    void Set(string endpointKey, string fingerprint);
}

/// <summary>What a trust decision is about.</summary>
/// <param name="Endpoint">The server.</param>
/// <param name="Kind">"SSH host key" or "TLS certificate".</param>
/// <param name="Fingerprint">The SHA-256 fingerprint to show.</param>
/// <param name="Known">The fingerprint saved earlier, or null if the server is new. When it differs the server has changed - or someone is in between.</param>
public sealed record TrustRequest(RemoteEndpoint Endpoint, string Kind, string Fingerprint, string? Known);

/// <summary>Why a session could not be made or a call failed, in terms the provider turns into <see cref="Leviathan.Core.Providers.LocationError"/>.</summary>
public enum RemoteFailure
{
    /// <summary>Something else went wrong.</summary>
    Unknown,

    /// <summary>The server could not be reached (nothing listening, no route, name not found).</summary>
    Unreachable,

    /// <summary>The server did not answer in time.</summary>
    Timeout,

    /// <summary>The sign-in was refused, or a password is needed.</summary>
    AuthenticationRequired,

    /// <summary>The server's key or certificate is not the one trusted.</summary>
    UntrustedServer,

    /// <summary>There is no such path.</summary>
    NotFound,

    /// <summary>The account may not do that.</summary>
    AccessDenied,
}

/// <summary>A failure talking to a server, already classified.</summary>
public sealed class RemoteException : IOException
{
    /// <summary>Creates the exception.</summary>
    public RemoteException(RemoteFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;

    /// <summary>What went wrong.</summary>
    public RemoteFailure Failure { get; }

    /// <summary>True when trying again may work (spec §3.3).</summary>
    public bool IsTransient => Failure is RemoteFailure.Unreachable or RemoteFailure.Timeout;
}
