using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.App;

/// <summary>Describes one local file for the tag folders (spec §11.2): a single stat, null when the file is gone or cannot be read.</summary>
internal static class TagFileStat
{
    /// <summary>The entry for the file at <paramref name="path"/>, or null.</summary>
    public static FileEntry? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || !LocationPath.TryParse(path, out var location))
            {
                return null;
            }

            return new FileEntry(info.Name, location, FileEntryKind.File, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
