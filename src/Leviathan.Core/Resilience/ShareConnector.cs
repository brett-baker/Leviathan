using Leviathan.Core.Paths;

namespace Leviathan.Core.Resilience;

/// <summary>How an attempt to connect to a share ended.</summary>
public enum ConnectOutcome
{
    /// <summary>The connection is up.</summary>
    Connected,

    /// <summary>There was nothing to do (a local drive, or the drive was already connected).</summary>
    NotNeeded,

    /// <summary>The user closed the sign-in dialog.</summary>
    Cancelled,

    /// <summary>Windows refused; <see cref="ConnectResult.Message"/> says why.</summary>
    Failed,
}

/// <summary>The result of <see cref="IShareConnector"/>.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">Windows' reason for a failure, in words; null otherwise.</param>
public sealed record ConnectResult(ConnectOutcome Outcome, string? Message = null)
{
    /// <summary>True when the share can be used now.</summary>
    public bool Succeeded => Outcome is ConnectOutcome.Connected or ConnectOutcome.NotNeeded;
}

/// <summary>
/// Brings a share back or signs in to it with the help of Windows (spec §3.3 Reconnect). Leviathan never stores a password:
/// reconnecting uses the credentials Windows Credential Manager already holds for the server, and signing in shows Windows' own
/// credential dialog, whose "Remember my credentials" box stores the password in Credential Manager, not in Leviathan. A password
/// typed there goes straight to Windows and is wiped from memory afterwards.
/// </summary>
public interface IShareConnector
{
    /// <summary>
    /// Reconnects the mapped drive <paramref name="driveLetter"/> to the share it was mapped to, using the credentials Windows holds.
    /// <see cref="ConnectOutcome.NotNeeded"/> for a drive that is not a mapping, or is connected already.
    /// </summary>
    Task<ConnectResult> ReconnectDriveAsync(char driveLetter, CancellationToken cancellationToken);

    /// <summary>
    /// Shows the Windows sign-in dialog for the server behind <paramref name="location"/>, owned by the window <paramref name="owner"/>,
    /// and connects with what the user types. Runs off the calling thread; may take as long as the user does.
    /// </summary>
    Task<ConnectResult> SignInAsync(LocationPath location, nint owner, CancellationToken cancellationToken);
}
