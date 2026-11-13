using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Leviathan.Core.Archives;
using Leviathan.Core.Paths;

namespace Leviathan.Providers.Archive;

/// <summary>
/// Unpacks one entry of an archive into a scratch folder so something that only knows file paths can use it: the default app when
/// the user opens a file inside an archive, the preview pane (spec §9.1, §15.4). Each entry is written to a
/// <c>.leviathan-part</c> file and renamed when complete (spec §3.4), so a half-unpacked file is never mistaken for a whole one. The
/// scratch folder is keyed by the archive's size and time, so a changed archive does not serve old files. <see cref="Clean"/>
/// removes old scratch files.
/// </summary>
public sealed class ArchiveStager(ArchiveIndex index, IFileSystem fileSystem, string stageRoot, Func<LocationPath, string>? toOsPath = null, TimeProvider? time = null) : IArchiveStager
{
    private readonly Func<LocationPath, string> _toOsPath = toOsPath ?? (l => l.ToWin32Path());
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Where scratch files go.</summary>
    public string StageRoot => stageRoot;

    /// <inheritdoc />
    public async Task<string> StageAsync(LocationPath entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (ArchiveKinds.Split(entry) is not { Inner.Count: > 0 } split)
        {
            return _toOsPath(entry);
        }

        var archivePath = _toOsPath(split.Archive);
        if (!await Task.Run(() => index.IsArchiveFile(archivePath), cancellationToken).ConfigureAwait(false))
        {
            // A real folder with an archive-like name.
            return _toOsPath(entry);
        }

        var contents = await index.GetAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var item = ArchiveFolder.Find(contents.Entries, split.InnerPath)
            ?? throw new ArchiveException(ArchiveError.EntryNotFound, $"{entry.Name} is not in {split.Archive.Name}.");

        var info = fileSystem.FileInfo.New(archivePath);
        var folder = fileSystem.Path.Combine(stageRoot, Fingerprint(archivePath, info.Length, info.LastWriteTimeUtc.Ticks));
        var target = fileSystem.Path.Combine(folder, fileSystem.Path.Combine([.. split.Inner.Select(Safe)]));
        if (fileSystem.File.Exists(target) && fileSystem.FileInfo.New(target).Length == item.Size)
        {
            fileSystem.File.SetLastAccessTimeUtc(target, _time.GetUtcNow().UtcDateTime);
            return target;
        }

        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(target)!);
        var part = target + ".leviathan-part";
        try
        {
            using (var stream = fileSystem.File.Create(part))
            {
                await index.Reader.ExtractEntryAsync(archivePath, item.Path, stream, null, cancellationToken).ConfigureAwait(false);
            }

            fileSystem.File.Move(part, target, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        return target;
    }

    /// <summary>Deletes scratch folders not used for <paramref name="olderThan"/>. Returns how many were removed.</summary>
    public int Clean(TimeSpan olderThan)
    {
        if (!fileSystem.Directory.Exists(stageRoot))
        {
            return 0;
        }

        var cutoff = _time.GetUtcNow().UtcDateTime - olderThan;
        var removed = 0;
        foreach (var folder in fileSystem.Directory.EnumerateDirectories(stageRoot))
        {
            try
            {
                var newest = fileSystem.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Select(f => fileSystem.File.GetLastAccessTimeUtc(f))
                    .DefaultIfEmpty(fileSystem.Directory.GetLastWriteTimeUtc(folder))
                    .Max();
                if (newest < cutoff)
                {
                    fileSystem.Directory.Delete(folder, recursive: true);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use by a program the user opened it with; try again next time.
            }
        }

        return removed;
    }

    private static string Fingerprint(string archivePath, long length, long ticks)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{archivePath.ToUpperInvariant()}|{length}|{ticks}"));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    // An entry name from an archive is not trusted: no drive letters, no colons, no ".." that would climb out of the folder.
    private static string Safe(string segment)
    {
        var cleaned = string.Concat(segment.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return cleaned is "" or "." or ".." ? "_" : cleaned;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (fileSystem.File.Exists(path))
            {
                fileSystem.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for Clean.
        }
    }
}
