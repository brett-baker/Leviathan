using System.ComponentModel;
using System.Runtime.InteropServices;
using Leviathan.Core.Duplicates;

namespace Leviathan.Interop;

/// <summary>
/// Hard links for the duplicate clean-up (spec §15.2). The link is made beside the copy under a temporary name and then swapped in,
/// so the copy's path never stops existing; if any step fails the temporary file is removed and the copy is untouched. Undoing a link
/// copies the contents back to a file of its own, again beside it and swapped in. All of it runs on the thread pool.
/// </summary>
public sealed partial class FileHardLinker : IHardLinker
{
    private const string LinkSuffix = ".leviathan-link";

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFileName, string existingFileName, nint securityAttributes);

    /// <inheritdoc />
    public Task<string?> ReplaceWithLinkAsync(string keepPath, string extraPath, CancellationToken cancellationToken) =>
        Task.Run<string?>(() =>
        {
            var temp = extraPath + LinkSuffix;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(temp))
                {
                    return $"'{Path.GetFileName(extraPath)}': a leftover '{Path.GetFileName(temp)}' is in the way.";
                }

                if (new FileInfo(keepPath).Length != new FileInfo(extraPath).Length)
                {
                    return $"'{Path.GetFileName(extraPath)}' changed size since the search: it was not linked.";
                }

                if (!CreateHardLink(Extended(temp), Extended(keepPath), 0))
                {
                    return $"'{Path.GetFileName(extraPath)}' could not be linked: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                }

                // A read-only copy cannot be replaced by a rename, so the flag is cleared first; the link then carries the kept file's own attributes.
                var attributes = File.GetAttributes(extraPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(extraPath, attributes & ~FileAttributes.ReadOnly);
                }

                File.Move(temp, extraPath, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                return $"'{Path.GetFileName(extraPath)}' could not be linked: {ex.Message}";
            }
        }, cancellationToken);

    /// <inheritdoc />
    public Task<string?> MakeIndependentAsync(string path, CancellationToken cancellationToken) =>
        Task.Run<string?>(() =>
        {
            var temp = path + ".leviathan-copy";
            try
            {
                File.Copy(path, temp, overwrite: true);
                File.Move(temp, path, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                return $"'{Path.GetFileName(path)}' could not be made a separate copy again: {ex.Message}";
            }
        }, cancellationToken);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover is reported the next time.
        }
    }

    /// <summary>The path in the form that lifts the 260 character limit.</summary>
    private static string Extended(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;
}
