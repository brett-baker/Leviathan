namespace Leviathan.Core.Paths;

/// <summary>What kind of location a <see cref="LocationPath"/> points at.</summary>
public enum LocationKind
{
    /// <summary>A path on a local or mapped drive, e.g. <c>C:\Users</c>.</summary>
    Drive,

    /// <summary>A Windows network path, e.g. <c>\\server\share\folder</c>.</summary>
    Unc,

    /// <summary><c>sftp://</c> location.</summary>
    Sftp,

    /// <summary><c>ftp://</c> location.</summary>
    Ftp,

    /// <summary><c>ftps://</c> location.</summary>
    Ftps,

    /// <summary><c>nfs://</c> location.</summary>
    Nfs,

    /// <summary>A shell location such as <c>shell:Downloads</c>, resolved by the shell layer.</summary>
    Shell,
}
