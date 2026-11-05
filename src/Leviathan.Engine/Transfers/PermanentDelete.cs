using System.IO.Abstractions;

namespace Leviathan.Engine.Transfers;

/// <summary>Deletes a file or folder for good (spec §12.5), the one place that knows how: used by Shift+Delete and by purging the bin.</summary>
internal static class PermanentDelete
{
    /// <summary>Deletes <paramref name="path"/>, read-only files included. A junction or symbolic link is removed itself; what it points at is never touched.</summary>
    public static void Run(IFileSystem fs, string path, bool isFolder)
    {
        if (!isFolder)
        {
            ClearReadOnly(fs, path);
            fs.File.Delete(path);
            return;
        }

        if (fs.File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            fs.Directory.Delete(path, recursive: false);
            return;
        }

        foreach (var file in fs.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            ClearReadOnly(fs, file);
        }

        fs.Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnly(IFileSystem fs, string path)
    {
        var attributes = fs.File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            fs.File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
