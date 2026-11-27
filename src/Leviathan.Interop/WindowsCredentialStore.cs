using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Security.Credentials;

namespace Leviathan.Interop;

/// <summary>
/// Windows Credential Manager for one secret under a target name, protected by the user's sign-in and kept for this user on this PC.
/// The learning servers' API keys use it (triage learning plan), so neither the settings file nor a log ever holds a key. Same approach
/// as <see cref="WindowsRemoteSecrets"/>.
/// </summary>
public static class WindowsCredentialStore
{
    /// <summary>The secret kept under <paramref name="target"/>, or null when there is none.</summary>
    public static unsafe string? Get(string target)
    {
        if (!PInvoke.CredRead(target, CRED_TYPE.CRED_TYPE_GENERIC, out var credential))
        {
            return null;
        }

        try
        {
            if (credential->CredentialBlobSize == 0 || credential->CredentialBlob is null)
            {
                return null;
            }

            return Encoding.Unicode.GetString(credential->CredentialBlob, (int)credential->CredentialBlobSize);
        }
        finally
        {
            PInvoke.CredFree(credential);
        }
    }

    /// <summary>Keeps <paramref name="secret"/> under <paramref name="target"/>; null or empty forgets it.</summary>
    public static unsafe void Set(string target, string userName, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            PInvoke.CredDelete(target, CRED_TYPE.CRED_TYPE_GENERIC);
            return;
        }

        var blob = Encoding.Unicode.GetBytes(secret);
        fixed (char* targetName = target)
        fixed (char* user = userName)
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
                throw new IOException("Windows could not save the key: " + Marshal.GetLastPInvokeErrorMessage());
            }
        }

        Array.Clear(blob);
    }
}
