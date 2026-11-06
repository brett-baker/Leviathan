using System.ComponentModel;
using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WNet;
using Windows.Win32.Security.Credentials;

namespace Leviathan.Interop;

/// <summary>
/// <see cref="IShareConnector"/> over the Windows networking and credential APIs (spec §3.3 Reconnect). Reconnecting hands the mapped
/// drive's remembered share to <c>WNetAddConnection2</c> without a user or password, so Windows uses what Credential Manager holds
/// for the server. Signing in shows Windows' own credential dialog (<c>CredUIPromptForCredentials</c>) and passes what was typed to
/// <c>WNetAddConnection2</c>; the dialog's "Remember my credentials" box stores it in Credential Manager through
/// <c>CredUIConfirmCredentials</c>, so Leviathan keeps nothing. The password buffers are wiped as soon as the call returns.
/// </summary>
public sealed class WindowsShareConnector : IShareConnector
{
    private const int NoError = 0;
    private const int AlreadyAssigned = 85;
    private const int Cancelled = 1223;
    private const int SessionCredentialConflict = 1219;
    private const int MaxUserName = 513;
    private const int MaxPassword = 257;

    /// <inheritdoc />
    public Task<ConnectResult> ReconnectDriveAsync(char driveLetter, CancellationToken cancellationToken) =>
        Task.Run(() => Reconnect(driveLetter), cancellationToken);

    /// <inheritdoc />
    public Task<ConnectResult> SignInAsync(LocationPath location, nint owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var done = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The credential dialog wants a thread with its own message loop; a short-lived STA thread keeps it off the pool and the UI.
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(SignIn(location, owner));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                done.TrySetResult(new ConnectResult(ConnectOutcome.Failed, ex.Message));
            }
        })
        {
            IsBackground = true,
            Name = "Leviathan sign-in",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ConnectResult> MapDriveAsync(char driveLetter, string shareRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareRoot);
        if (!char.IsAsciiLetter(driveLetter) || !shareRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Task.FromResult(new ConnectResult(ConnectOutcome.Failed, "A drive letter and a \\\\server\\share path are needed."));
        }

        var local = char.ToUpperInvariant(driveLetter) + ":";
        return Task.Run(() => Connect(local, shareRoot, userName: null, password: null, remember: true), cancellationToken);
    }

    private static ConnectResult Reconnect(char driveLetter)
    {
        if (!char.IsAsciiLetter(driveLetter))
        {
            return new ConnectResult(ConnectOutcome.NotNeeded);
        }

        var local = char.ToUpperInvariant(driveLetter) + ":";

        // A local disk has no remembered share, so there is nothing to bring back.
        if (WindowsDriveMappings.QueryUnc(local) is not { Length: > 0 } remote)
        {
            return new ConnectResult(ConnectOutcome.NotNeeded);
        }

        return Connect(local, remote, userName: null, password: null);
    }

    private static unsafe ConnectResult Connect(string? local, string remote, string? userName, string? password, bool remember = false)
    {
        fixed (char* remotePtr = remote)
        fixed (char* localPtr = local)
        {
            var resource = new NETRESOURCEW
            {
                // \\server\IPC$ is the server's own session, not a disk; asking for a disk there fails with "bad device type".
                dwType = remote.EndsWith(IpcShare, StringComparison.OrdinalIgnoreCase) ? NET_RESOURCE_TYPE.RESOURCETYPE_ANY : NET_RESOURCE_TYPE.RESOURCETYPE_DISK,
                lpRemoteName = remotePtr,
                lpLocalName = local is null ? default : localPtr,
            };
            // CONNECT_UPDATE_PROFILE makes Windows bring the drive back at the next sign-in.
            var error = PInvoke.WNetAddConnection2W(resource, password, userName, remember ? NET_CONNECT_FLAGS.CONNECT_UPDATE_PROFILE : 0);
            return (int)error switch
            {
                NoError => new ConnectResult(ConnectOutcome.Connected),
                AlreadyAssigned when remember => new ConnectResult(ConnectOutcome.Failed, $"{local} is already in use. Pick another drive letter."),
                AlreadyAssigned => new ConnectResult(ConnectOutcome.NotNeeded),
                SessionCredentialConflict => new ConnectResult(ConnectOutcome.Failed, "Windows already has a connection to this server under another user name. Disconnect it first (net use \\\\server /delete), then sign in again."),
                _ => new ConnectResult(ConnectOutcome.Failed, new Win32Exception((int)error).Message),
            };
        }
    }

    private static unsafe ConnectResult SignIn(LocationPath location, nint owner)
    {
        var share = ShareRootOf(location);
        if (share is null)
        {
            return new ConnectResult(ConnectOutcome.NotNeeded);
        }

        var server = ShareHealthRegistry.ServerOf(LocationPath.Parse(share)) ?? share;
        var userName = new char[MaxUserName];
        var password = new char[MaxPassword];
        try
        {
            fixed (char* captionPtr = "Leviathan")
            fixed (char* messagePtr = $"Sign in to {server}")
            fixed (char* userPtr = userName)
            fixed (char* passwordPtr = password)
            fixed (char* targetPtr = server)
            {
                var info = new CREDUI_INFOW
                {
                    cbSize = (uint)sizeof(CREDUI_INFOW),
                    hwndParent = new HWND(owner),
                    pszCaptionText = captionPtr,
                    pszMessageText = messagePtr,
                };
                BOOL save = false;
                var asked = PInvoke.CredUIPromptForCredentials(
                    &info,
                    targetPtr,
                    null,
                    0,
                    userPtr,
                    (uint)userName.Length,
                    passwordPtr,
                    (uint)password.Length,
                    &save,
                    CREDUI_FLAGS.CREDUI_FLAGS_ALWAYS_SHOW_UI | CREDUI_FLAGS.CREDUI_FLAGS_EXPECT_CONFIRMATION | CREDUI_FLAGS.CREDUI_FLAGS_SHOW_SAVE_CHECK_BOX
                    | CREDUI_FLAGS.CREDUI_FLAGS_SERVER_CREDENTIAL);
                if ((int)asked == Cancelled)
                {
                    return new ConnectResult(ConnectOutcome.Cancelled);
                }

                if ((int)asked != NoError)
                {
                    return new ConnectResult(ConnectOutcome.Failed, new Win32Exception((int)asked).Message);
                }

                var result = Connect(null, share, new string(userPtr), new string(passwordPtr));

                // Windows stores the credentials itself: with the confirmation expected it does so only when they worked and the
                // "Remember my credentials" box was ticked, and throws them away otherwise.
                _ = PInvoke.CredUIConfirmCredentials(server, result.Outcome == ConnectOutcome.Connected);
                return result;
            }
        }
        finally
        {
            Array.Clear(userName);
            Array.Clear(password);
        }
    }

    private const string IpcShare = @"\IPC$";

    // \\server\share for a share path, the share a mapped drive points at, or \\server\IPC$ for a server on its own (the way to sign
    // in to a server before any share is chosen, as Explorer does: the session it opens is the one that then lists the shares).
    internal static string? ShareRootOf(LocationPath location)
    {
        if (location.Kind == LocationKind.Unc)
        {
            return location.Share is null ? location.Root + IpcShare : location.Root;
        }

        return location.Kind == LocationKind.Drive && location.Root.Length >= 2 ? WindowsDriveMappings.QueryUnc(location.Root[..2]) : null;
    }
}
