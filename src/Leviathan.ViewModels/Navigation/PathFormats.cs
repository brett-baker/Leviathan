using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Navigation;

/// <summary>The "Copy as" variants of the address bar's context menu (spec §5.2).</summary>
public enum CopyAsFormat
{
    /// <summary>The path as shown.</summary>
    Path,

    /// <summary>A mapped drive letter replaced by its UNC share (<c>Z:\a</c> to <c>\\server\share\a</c>).</summary>
    Unc,

    /// <summary>The path in double quotes.</summary>
    Quoted,

    /// <summary>A <c>file://</c> URI with percent-encoding.</summary>
    FileUri,
}

/// <summary>Knows which drive letters are mapped network drives.</summary>
public interface IDriveMappings
{
    /// <summary>Returns the share a drive letter maps to (<c>Z:</c> gives <c>\\nas\media</c>), or null for a local drive.</summary>
    string? TryGetUncRoot(string driveRoot);
}

/// <summary>Formats locations for the clipboard.</summary>
public static class PathFormats
{
    /// <summary>Formats <paramref name="location"/>. Without a mapping, the UNC form is the plain path.</summary>
    public static string Format(LocationPath location, CopyAsFormat format, IDriveMappings? mappings = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        return format switch
        {
            CopyAsFormat.Path => location.Value,
            CopyAsFormat.Quoted => "\"" + location.Value + "\"",
            CopyAsFormat.Unc => ToUnc(location, mappings),
            CopyAsFormat.FileUri => ToFileUri(location),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static string ToUnc(LocationPath location, IDriveMappings? mappings)
    {
        if (location.Kind != LocationKind.Drive || mappings is null)
        {
            return location.Value;
        }

        var unc = mappings.TryGetUncRoot(location.Root.TrimEnd('\\'));
        if (unc is null)
        {
            return location.Value;
        }

        return location.Segments.Count == 0 ? unc : unc.TrimEnd('\\') + "\\" + string.Join('\\', location.Segments);
    }

    private static string ToFileUri(LocationPath location)
    {
        var encoded = string.Join('/', location.Segments.Select(Uri.EscapeDataString));
        switch (location.Kind)
        {
            case LocationKind.Drive:
                var drive = location.Root.TrimEnd('\\');
                return encoded.Length == 0 ? $"file:///{drive}/" : $"file:///{drive}/{encoded}";
            case LocationKind.Unc:
                var head = $"file://{location.Server}" + (location.Share is null ? string.Empty : "/" + Uri.EscapeDataString(location.Share));
                return encoded.Length == 0 ? head : head + "/" + encoded;
            default:
                return location.Value;
        }
    }
}
