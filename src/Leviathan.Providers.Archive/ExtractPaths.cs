namespace Leviathan.Providers.Archive;

/// <summary>
/// Turns a path stored in an archive into a path under the folder being extracted to, refusing anything that would land elsewhere
/// ("zip slip": <c>..\..\Windows\x.dll</c>, a drive letter, an absolute path) or that Windows cannot name (spec §15.4, §3.4). Pure.
/// </summary>
public static class ExtractPaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The full path for <paramref name="entryPath"/> (forward slashes) below <paramref name="root"/>, or null when the entry must not be
    /// written: <paramref name="problem"/> then says why.
    /// </summary>
    public static string? Resolve(string root, string entryPath, out string? problem)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        problem = null;
        var normalized = entryPath.Replace('\\', '/');
        if (normalized.StartsWith('/') || (normalized.Length > 1 && normalized[1] == ':'))
        {
            problem = "its path is absolute";
            return null;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            problem = "it has no name";
            return null;
        }

        foreach (var part in parts)
        {
            if (part is "." or "..")
            {
                problem = "its path climbs out of the folder";
                return null;
            }

            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' '))
            {
                problem = $"\"{part}\" is not a name Windows can use";
                return null;
            }

            var stem = part.Contains('.', StringComparison.Ordinal) ? part[..part.IndexOf('.', StringComparison.Ordinal)] : part;
            if (Reserved.Contains(stem))
            {
                problem = $"\"{part}\" is a reserved name";
                return null;
            }
        }

        var full = Path.GetFullPath(Path.Combine([root, .. parts]));
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + '\\';
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            problem = "its path leaves the folder";
            return null;
        }

        return full;
    }

    /// <summary>The name for a folder holding an archive's files: the archive's name without its extension (and without <c>.tar</c> before <c>.gz</c>).</summary>
    public static string FolderNameFor(string archiveFileName)
    {
        var name = Path.GetFileNameWithoutExtension(archiveFileName);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length == 0 ? "Extracted" : name.TrimEnd('.', ' ');
    }
}
