namespace Leviathan.Core.Archives;

/// <summary>One item stored in an archive, as a reader reports it.</summary>
/// <param name="Path">Path inside the archive with forward slashes, no leading or trailing slash.</param>
/// <param name="IsDirectory">True for a folder entry.</param>
/// <param name="Size">Unpacked size in bytes (0 for folders).</param>
/// <param name="Modified">Last write time, or null when the archive does not store one.</param>
/// <param name="Created">Creation time, or null.</param>
/// <param name="Encrypted">True when the entry needs a password to extract.</param>
public sealed record ArchiveEntryInfo(string Path, bool IsDirectory, long Size, DateTimeOffset? Modified, DateTimeOffset? Created, bool Encrypted = false)
{
    /// <summary>Normalizes an archive's own spelling of a path: backslashes become slashes, leading and trailing slashes go.</summary>
    public static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');
}

/// <summary>Why an archive could not be read.</summary>
public enum ArchiveError
{
    /// <summary>Damaged, truncated or not an archive at all.</summary>
    Unreadable,

    /// <summary>The kind is not supported by the reader.</summary>
    Unsupported,

    /// <summary>A password is needed.</summary>
    PasswordRequired,

    /// <summary>The password given is wrong.</summary>
    WrongPassword,

    /// <summary>The entry named is not in the archive.</summary>
    EntryNotFound,
}

/// <summary>An archive could not be read or an entry could not be extracted.</summary>
public sealed class ArchiveException : IOException
{
    /// <summary>Creates the exception.</summary>
    public ArchiveException(ArchiveError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    /// <summary>What went wrong.</summary>
    public ArchiveError Error { get; }
}
