using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Sidebar;

/// <summary>
/// What the Add connection dialog makes of the address the user typed (spec §7.1). SMB only for now: <c>nas</c>, <c>\\nas</c>,
/// <c>\\nas\media</c>, <c>//nas/media</c> and <c>smb://nas/media</c> all work; SFTP, FTP(S) and NFS arrive in Phase 4. Pure parsing, so
/// the dialog can validate as the user types and the rules are testable.
/// </summary>
public sealed record AddConnectionInput
{
    private AddConnectionInput()
    {
    }

    /// <summary>The server's host name or address.</summary>
    public string Host { get; private init; } = string.Empty;

    /// <summary>The share, or null when only a server was given.</summary>
    public string? Share { get; private init; }

    /// <summary>Where to go: the share (or a folder in it) when one was given, else the server's root.</summary>
    public LocationPath Location { get; private init; } = null!;

    /// <summary>True when a share was given, which a drive letter needs.</summary>
    public bool HasShare => Share is not null;

    /// <summary>The share as <c>\\server\share</c> (what a drive letter maps to), or null.</summary>
    public string? ShareRoot => Share is null ? null : @$"\\{Host}\{Share}";

    /// <summary>Reads <paramref name="text"/>. Returns null and a message for the dialog when it is not a usable SMB address.</summary>
    public static AddConnectionInput? Parse(string? text, out string error)
    {
        var address = (text ?? string.Empty).Trim();
        if (address.Length == 0)
        {
            error = "Type the name or address of the server, such as diskstation or \\\\192.168.1.10\\media.";
            return null;
        }

        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            if (!address[..scheme].Equals("smb", StringComparison.OrdinalIgnoreCase))
            {
                error = $"{address[..scheme]}:// connections arrive in a later version; only SMB (\\\\server\\share) works now.";
                return null;
            }

            address = address[(scheme + 3)..];
        }

        var parts = address.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !SavedConnectionList.IsValidHost(parts[0]))
        {
            error = "That is not a server name or address. Leave out spaces and characters such as : * ? \" < > |.";
            return null;
        }

        var unc = @"\\" + string.Join('\\', parts);
        if (!LocationPath.TryParse(unc, out var location))
        {
            error = "Windows cannot use that as a network path.";
            return null;
        }

        error = string.Empty;
        return new AddConnectionInput
        {
            Host = parts[0],
            Share = parts.Length > 1 ? parts[1] : null,
            Location = location,
        };
    }
}
