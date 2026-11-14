using System.IO.Abstractions;
using Leviathan.Core.Paths;

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

        DeleteFolder(fs, path);
    }

    // Walked folder by folder, not with one recursive call, so that every name goes through LocationPath.ForIo: a file or folder called "x."
    // or "con" (they come from shares and other systems) is only reachable through the extended prefix, and a recursive delete would
    // try them by their plain names and stop with "path not found".
    private static void DeleteFolder(IFileSystem fs, string folder)
    {
        if (fs.File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
        {
            fs.Directory.Delete(folder, recursive: false);
            return;
        }

        foreach (var file in fs.Directory.EnumerateFiles(folder).ToList())
        {
            var io = LocationPath.ForIo(file);
            ClearReadOnly(fs, io);
            fs.File.Delete(io);
        }

        foreach (var child in fs.Directory.EnumerateDirectories(folder).ToList())
        {
            DeleteFolder(fs, LocationPath.ForIo(child));
        }

        fs.Directory.Delete(folder, recursive: false);
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
