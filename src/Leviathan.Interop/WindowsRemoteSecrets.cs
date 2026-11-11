using System.Runtime.InteropServices;
using System.Text;
using Leviathan.Providers.Remote;
using Windows.Win32;
using Windows.Win32.Security.Credentials;

namespace Leviathan.Interop;

/// <summary>
/// Keeps the passwords and key passphrases of SFTP, FTP and FTPS connections in Windows Credential Manager (spec §7.1, §3.3), under a target made from the
/// server and account, so Leviathan's own files hold no secrets and Windows protects them with the user's sign-in. Generic credentials, kept for this user on this PC.
/// </summary>
public sealed class WindowsRemoteSecrets : IRemoteSecrets
{
    private const string Prefix = "Leviathan:";

    /// <summary>The credential target for an endpoint.</summary>
    public static string TargetOf(RemoteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Prefix + endpoint.Key;
    }

    /// <inheritdoc />
    public unsafe string? Get(RemoteEndpoint endpoint)
    {
        if (!PInvoke.CredRead(TargetOf(endpoint), CRED_TYPE.CRED_TYPE_GENERIC, out var credential))
        {
            return null;
        }

        try
        {
            if (credential->CredentialBlobSize == 0 || credential->CredentialBlob is null)
            {
                return string.Empty;
            }

            return Encoding.Unicode.GetString(credential->CredentialBlob, (int)credential->CredentialBlobSize);
        }
        finally
        {
            PInvoke.CredFree(credential);
        }
    }

    /// <inheritdoc />
    public unsafe void Set(RemoteEndpoint endpoint, string? secret)
    {
        var target = TargetOf(endpoint);
        if (secret is null)
        {
            PInvoke.CredDelete(target, CRED_TYPE.CRED_TYPE_GENERIC);
            return;
        }

        var blob = Encoding.Unicode.GetBytes(secret);
        fixed (char* targetName = target)
        fixed (char* user = endpoint.User ?? string.Empty)
        fixed (byte* data = blob)
        {
            var credential = new CREDENTIALW
            {
                Type = CRED_TYPE.CRED_TYPE_GENERIC,
                TargetName = targetName,
                UserName = user,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!PInvoke.CredWrite(in credential, 0))
            {
                throw new IOException("Windows could not save the password: " + Marshal.GetLastPInvokeErrorMessage());
            }
        }

        Array.Clear(blob);
    }
}
