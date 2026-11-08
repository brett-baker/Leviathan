using System.Text.Json;

namespace Leviathan.ViewModels.Triage;

/// <summary>
/// Colour tags in the <c>:leviathan.tags</c> alternate data stream of the file (spec §11.2), a small JSON document with the tag ids and the
/// time they were set. The stream travels with the file on NTFS and on SMB shares that keep streams (Synology and QNAP do by default).
/// Writing a stream changes the file's modified time, which would show up as an edit and wake backup software, so the time is put back
/// (rule 1). A file that is read-only is made writable for the moment and made read-only again. A location with no streams (FAT, exFAT,
/// SFTP, NFS) cannot hold a tag: that is an <see cref="IOException"/> with a plain message, never a silently lost tag; the index of
/// slice F2 is what will carry tags there. All disk work runs off the calling thread.
/// </summary>
public sealed class AlternateStreamTagStore(TimeProvider? time = null) : ITagStore
{
    /// <summary>The name of the stream.</summary>
    public const string StreamName = "leviathan.tags";

    private const int SchemaVersion = 1;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record Document(int V, int[] Tags, DateTimeOffset At);

    /// <inheritdoc />
    public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run<IReadOnlySet<int>>(() => Read(path), cancellationToken);
    }

    /// <inheritdoc />
    public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(tags);
        return Task.Run(() => Write(path, tags), cancellationToken);
    }

    private static string StreamPath(string path) => path + ":" + StreamName;

    private static HashSet<int> Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{Path.GetFileName(path)} is not there.", path);
        }

        try
        {
            using var stream = new FileStream(StreamPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = JsonSerializer.Deserialize<Document>(stream, JsonOptions);
            return document?.Tags is { } tags ? new HashSet<int>(tags) : new HashSet<int>();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new HashSet<int>(); // the file is there but has no stream: no tags
        }
        catch (JsonException)
        {
            return new HashSet<int>(); // damaged stream: treated as no tags, and the next write replaces it
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException)
        {
            throw Unsupported(path, ex);
        }
    }

    private void Write(string path, IReadOnlySet<int> tags)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{Path.GetFileName(path)} is not there.", path);
        }

        var modified = File.GetLastWriteTimeUtc(path);
        var attributes = File.GetAttributes(path);
        try
        {
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            if (tags.Count == 0)
            {
                DeleteStream(path);
            }
            else
            {
                using var stream = new FileStream(StreamPath(path), FileMode.Create, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, new Document(SchemaVersion, [.. tags.Order()], _time.GetUtcNow()), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException || (ex is IOException && ex is not FileNotFoundException))
        {
            throw Unsupported(path, ex);
        }
        finally
        {
            // Put back what writing the stream changed: the modified time (spec §11.2 rule 1) and the read-only flag.
            try
            {
                File.SetLastWriteTimeUtc(path, modified);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(path, attributes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The tag is written; a time that could not be put back is not worth failing a decision over.
            }
        }
    }

    private static void DeleteStream(string path)
    {
        try
        {
            File.Delete(StreamPath(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing to remove.
        }
    }

    private static IOException Unsupported(string path, Exception inner) =>
        new($"{Path.GetFileName(path)} is on a location that cannot hold tags (it has no alternate data streams).", inner);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
