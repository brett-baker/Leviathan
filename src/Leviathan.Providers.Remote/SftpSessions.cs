using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Leviathan.Providers.Remote;

/// <summary>Connects to SFTP servers with SSH.NET (spec §7.1, §2.4): password or key, a host key checked against the ones trusted, a time limit on connecting.</summary>
/// <param name="secrets">Where the password or key passphrase is kept.</param>
/// <param name="knownHosts">The host keys trusted so far.</param>
/// <param name="trust">Asks whether to trust a server seen for the first time; null trusts it and remembers it. A key that differs from the one remembered is always refused.</param>
public sealed class SftpSessionFactory(IRemoteSecrets secrets, IKnownHosts knownHosts, Func<TrustRequest, Task<bool>>? trust = null) : IRemoteSessionFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<IRemoteSession> ConnectAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var secret = secrets.Get(endpoint);
        var methods = new List<AuthenticationMethod>();
        try
        {
            if (endpoint.KeyFile is { Length: > 0 } keyFile)
            {
                methods.Add(new PrivateKeyAuthenticationMethod(endpoint.User ?? string.Empty, string.IsNullOrEmpty(secret) ? new PrivateKeyFile(keyFile) : new PrivateKeyFile(keyFile, secret)));
            }
            else
            {
                methods.Add(new PasswordAuthenticationMethod(endpoint.User ?? string.Empty, secret ?? string.Empty));
                methods.Add(new KeyboardInteractiveAuthenticationMethod(endpoint.User ?? string.Empty));
            }
        }
        catch (Exception ex) when (ex is SshException or IOException or ArgumentException)
        {
            throw new RemoteException(RemoteFailure.AuthenticationRequired, "The key file could not be read or needs a passphrase.", ex);
        }

        var info = new ConnectionInfo(endpoint.Host, endpoint.Port, endpoint.User ?? string.Empty, [.. methods]) { Timeout = ConnectTimeout };
        if (methods[^1] is KeyboardInteractiveAuthenticationMethod interactive)
        {
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = secret ?? string.Empty;
                }
            };
        }

        var client = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30), OperationTimeout = TimeSpan.FromSeconds(60) };
        UntrustedServer? refused = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = "SHA256:" + e.FingerPrintSHA256;
            var known = knownHosts.Get(endpoint.Key);
            if (known is not null && known != fingerprint)
            {
                refused = new UntrustedServer(fingerprint, known);
                e.CanTrust = false;
                return;
            }

            if (known is null)
            {
                var request = new TrustRequest(endpoint, "SSH host key", fingerprint, null);
                // The event is synchronous; a prompt is waited for here, on a pool thread, never the UI thread.
                var accepted = trust is null || trust(request).GetAwaiter().GetResult();
                if (!accepted)
                {
                    refused = new UntrustedServer(fingerprint, null);
                    e.CanTrust = false;
                    return;
                }

                knownHosts.Set(endpoint.Key, fingerprint);
            }

            e.CanTrust = true;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw SftpErrors.Wrap(ex, endpoint, refused);
        }

        return new SftpSession(client);
    }

    private sealed record UntrustedServer(string Fingerprint, string? Known);

    internal static class SftpErrors
    {
        internal static RemoteException Wrap(Exception ex, RemoteEndpoint endpoint, object? refused = null)
        {
            if (refused is UntrustedServer server)
            {
                return new RemoteException(RemoteFailure.UntrustedServer, server.Known is null
                    ? $"{endpoint.Host} was not trusted."
                    : $"The key of {endpoint.Host} is not the one trusted before ({server.Known}). Someone may be in between, or the server was reinstalled. Remove the saved connection to trust it again.", ex);
            }

            return SftpSession.Classify(ex, endpoint.Host);
        }
    }
}

/// <summary>An open SFTP connection (spec §7.1).</summary>
public sealed class SftpSession(SftpClient client) : IRemoteSession
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public bool IsConnected => client.IsConnected;

    internal static RemoteException Classify(Exception ex, string host) => ex switch
    {
        RemoteException already => already,
        SshAuthenticationException => new RemoteException(RemoteFailure.AuthenticationRequired, $"{host} refused the sign-in. Check the user name and password.", ex),
        SftpPathNotFoundException => new RemoteException(RemoteFailure.NotFound, "That path was not found on the server.", ex),
        SftpPermissionDeniedException => new RemoteException(RemoteFailure.AccessDenied, "The server does not allow that.", ex),
        SshOperationTimeoutException or TimeoutException => new RemoteException(RemoteFailure.Timeout, $"{host} did not answer in time.", ex),
        SshConnectionException or SocketException => new RemoteException(RemoteFailure.Unreachable, $"{host} could not be reached.", ex),
        OperationCanceledException => throw ex,
        _ => new RemoteException(RemoteFailure.Unknown, ex.Message, ex),
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var items = new List<RemoteItem>();
            await foreach (var file in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                items.Add(ToItem(file));
            }

            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.ConnectionInfo.Host);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!await client.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ToItem(await client.GetAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.ConnectionInfo.Host);
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await client.OpenAsync(path, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.ConnectionInfo.Host);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            await client.UploadFileAsync(content, path, true, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.ConnectionInfo.Host);
        }
    }

    /// <inheritdoc />
    public async Task RenameAsync(string from, string to, CancellationToken cancellationToken)
    {
        try
        {
            await client.RenameFileAsync(from, to, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.ConnectionInfo.Host);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        try
        {
            if (isFolder)
            {
                await client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.ConnectionInfo.Host);
        }
    }

    /// <inheritdoc />
    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await client.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.ConnectionInfo.Host);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException)
        {
            // Closing a connection that is already gone is fine.
        }

        return ValueTask.CompletedTask;
    }

    private static RemoteItem ToItem(ISftpFile file) =>
        new(file.Name, file.FullName, file.IsDirectory, file.IsDirectory ? 0 : file.Length, new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc)), file.IsSymbolicLink);
}
