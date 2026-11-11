using System.Net.Sockets;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Leviathan.Providers.Remote;

/// <summary>Connects to FTP and FTPS servers with FluentFTP (spec §7.1, §2.4): passive mode, a time limit, and for FTPS a certificate checked against the one trusted.</summary>
/// <param name="secrets">Where the password is kept.</param>
/// <param name="knownHosts">The certificate fingerprints trusted so far.</param>
/// <param name="trust">Asks whether to trust a certificate seen for the first time; null trusts it and remembers it. A different certificate than the one remembered is always refused.</param>
public sealed class FtpSessionFactory(IRemoteSecrets secrets, IKnownHosts knownHosts, Func<TrustRequest, Task<bool>>? trust = null) : IRemoteSessionFactory
{
    /// <inheritdoc />
    public async Task<IRemoteSession> ConnectAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var anonymous = string.IsNullOrEmpty(endpoint.User);
        var client = new AsyncFtpClient(endpoint.Host, anonymous ? "anonymous" : endpoint.User!, anonymous ? "leviathan@localhost" : secrets.Get(endpoint) ?? string.Empty, endpoint.Port);
        client.Config.ConnectTimeout = 10_000;
        client.Config.ReadTimeout = 30_000;
        client.Config.DataConnectionConnectTimeout = 10_000;
        client.Config.DataConnectionReadTimeout = 30_000;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.RetryAttempts = 1;
        client.Config.LogToConsole = false;
        string? refusal = null;
        if (endpoint.Protocol == RemoteProtocol.Ftps)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Auto;
            client.Config.ValidateAnyCertificate = false;
            client.ValidateCertificate += (_, e) =>
            {
                var fingerprint = "SHA256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(e.Certificate?.GetRawCertData() ?? []));
                var known = knownHosts.Get(endpoint.Key);
                if (known is not null)
                {
                    e.Accept = known == fingerprint;
                    if (!e.Accept)
                    {
                        refusal = $"The certificate of {endpoint.Host} is not the one trusted before ({known}). Remove the saved connection to trust it again.";
                    }

                    return;
                }

                var accepted = trust is null || trust(new TrustRequest(endpoint, "TLS certificate", fingerprint, null)).GetAwaiter().GetResult();
                e.Accept = accepted;
                if (accepted)
                {
                    knownHosts.Set(endpoint.Key, fingerprint);
                }
                else
                {
                    refusal = $"{endpoint.Host} was not trusted.";
                }
            };
        }
        else
        {
            client.Config.EncryptionMode = FtpEncryptionMode.None;
        }

        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (refusal is not null)
            {
                throw new RemoteException(RemoteFailure.UntrustedServer, refusal, ex);
            }

            throw FtpSession.Classify(ex, endpoint.Host);
        }

        return new FtpSession(client);
    }
}

/// <summary>An open FTP or FTPS connection (spec §7.1).</summary>
public sealed class FtpSession(AsyncFtpClient client) : IRemoteSession
{
    /// <inheritdoc />
    public bool IsConnected => client.IsConnected;

    internal static RemoteException Classify(Exception ex, string host)
    {
        if (ex is RemoteException already)
        {
            return already;
        }

        if (ex is OperationCanceledException)
        {
            throw ex;
        }

        var text = ex.Message;
        return ex switch
        {
            FtpAuthenticationException => new RemoteException(RemoteFailure.AuthenticationRequired, $"{host} refused the sign-in. Check the user name and password.", ex),
            TimeoutException or FtpCommandException { CompletionCode: "421" } => new RemoteException(RemoteFailure.Timeout, $"{host} did not answer in time.", ex),
            FtpCommandException { CompletionCode: "530" } => new RemoteException(RemoteFailure.AuthenticationRequired, $"{host} refused the sign-in.", ex),
            FtpCommandException { CompletionCode: "550" } => new RemoteException(text.Contains("ermission", StringComparison.OrdinalIgnoreCase) ? RemoteFailure.AccessDenied : RemoteFailure.NotFound, text, ex),
            FtpCommandException { CompletionCode: "553" } => new RemoteException(RemoteFailure.AccessDenied, text, ex),
            SocketException or FtpException { InnerException: SocketException } or IOException => new RemoteException(RemoteFailure.Unreachable, $"{host} could not be reached.", ex),
            _ => new RemoteException(RemoteFailure.Unknown, text, ex),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var listing = await client.GetListing(path, FtpListOption.Auto, cancellationToken).ConfigureAwait(false);
            return [.. listing.Where(i => i.Type is FtpObjectType.File or FtpObjectType.Directory or FtpObjectType.Link && i.Name is not ("." or "..")).Select(ToItem)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.Host);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (await client.DirectoryExists(path, cancellationToken).ConfigureAwait(false))
            {
                return new RemoteItem(path.TrimEnd('/').Split('/').LastOrDefault() ?? "/", path, true, 0, DateTimeOffset.UnixEpoch);
            }

            if (!await client.FileExists(path, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var size = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
            var modified = await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false);
            return new RemoteItem(path.Split('/').Last(), path, false, Math.Max(0, size), new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.Host);
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await client.OpenRead(path, FtpDataType.Binary, 0, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.Host);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            var status = await client.UploadStream(content, path, FtpRemoteExists.Overwrite, true, null, cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Failed)
            {
                throw new RemoteException(RemoteFailure.Unknown, "The server did not accept the file.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.Host);
        }
    }

    /// <inheritdoc />
    public async Task RenameAsync(string from, string to, CancellationToken cancellationToken)
    {
        try
        {
            if (!await client.MoveFile(from, to, FtpRemoteExists.Overwrite, cancellationToken).ConfigureAwait(false))
            {
                throw new RemoteException(RemoteFailure.NotFound, "The server could not rename it.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.Host);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        try
        {
            if (isFolder)
            {
                await client.DeleteDirectory(path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFile(path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.Host);
        }
    }

    /// <inheritdoc />
    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await client.CreateDirectory(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, client.Host);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex) when (ex is FtpException or ObjectDisposedException or SocketException or IOException)
        {
            // Closing a connection that is already gone is fine.
        }

        return ValueTask.CompletedTask;
    }

    private static RemoteItem ToItem(FtpListItem item)
    {
        var modified = item.Modified == default ? DateTimeOffset.UnixEpoch : new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc));
        return new RemoteItem(item.Name, item.FullName, item.Type == FtpObjectType.Directory, item.Type == FtpObjectType.Directory ? 0 : Math.Max(0, item.Size), modified, item.Type == FtpObjectType.Link);
    }
}
