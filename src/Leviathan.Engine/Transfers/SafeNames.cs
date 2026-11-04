namespace Leviathan.Engine.Transfers;

/// <summary>
/// Names that are fine on an NTFS disk but that a share or another file system refuses: reserved characters (a colon from a tag or a
/// time stamp), trailing dots and spaces, device names such as <c>CON</c>, and names over 255 characters (spec §12.2). The planner
/// uses <see cref="Sanitize"/> for every name it writes to a network destination, so the copy goes through instead of stopping on the
/// first such file, and the job's report lists what was renamed.
/// </summary>
public static class SafeNames
{
    private const int MaxLength = 255;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True when the name can be created as it is.</summary>
    public static bool IsValid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > MaxLength || name.EndsWith(' ') || name.EndsWith('.') && name is not "." and not "..")
        {
            return false;
        }

        if (name.Any(c => c < ' ' || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal)))
        {
            return false;
        }

        var stem = name.Contains('.', StringComparison.Ordinal) ? name[..name.IndexOf('.', StringComparison.Ordinal)] : name;
        return !Reserved.Contains(stem.TrimEnd());
    }

    /// <summary>The closest valid name: bad characters become <c>_</c>, trailing dots and spaces are dropped, device names get a leading <c>_</c>, long names are cut before the extension. A valid name comes back unchanged.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (IsValid(name))
        {
            return name;
        }

        var chars = name.Select(c => c < ' ' || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c).ToArray();
        var text = new string(chars).TrimEnd(' ', '.');
        if (text.Length == 0)
        {
            return "_";
        }

        var stem = text.Contains('.', StringComparison.Ordinal) ? text[..text.IndexOf('.', StringComparison.Ordinal)] : text;
        if (Reserved.Contains(stem.TrimEnd()))
        {
            text = "_" + text;
        }

        if (text.Length > MaxLength)
        {
            var dot = text.LastIndexOf('.');
            var extension = dot > 0 && text.Length - dot <= 20 ? text[dot..] : string.Empty;
            text = text[..(MaxLength - extension.Length)] + extension;
        }

        return text.TrimEnd(' ', '.') is { Length: > 0 } trimmed ? trimmed : "_";
    }
}
