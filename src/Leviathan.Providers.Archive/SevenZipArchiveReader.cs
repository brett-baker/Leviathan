using System.Runtime.InteropServices;
using Leviathan.Core.Archives;
using SevenZip;

namespace Leviathan.Providers.Archive;

/// <summary>
/// Reads zip, 7z, rar, iso, cab and most other archive kinds through 7-Zip (spec §15.4, §18), the way Files does: SevenZipSharp wraps
/// <c>7z.dll</c>, which sits beside the program. Each call opens its own extractor, because an extractor is not safe to share between
/// threads, and does its work on the thread pool. Extraction honours the token by failing the write that follows a cancel, which makes
/// 7-Zip stop.
/// </summary>
public sealed class SevenZipArchiveReader : IArchiveReader
{
    private static readonly Lock LibraryLock = new();
    private static string? _libraryError;
    private static bool _libraryReady;

    // SevenZipSharp reports this for entries with no stored name, such as a gzip made without the optional name field.
    private const string NamelessEntry = "[no name]";

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchiveEntryInfo>> ReadEntriesAsync(string archivePath, string? password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        return Task.Run<IReadOnlyList<ArchiveEntryInfo>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var extractor = Open(archivePath, password);
                return [.. Entries(extractor, archivePath).Select(e => e.Info)];
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ExtractEntryAsync(string archivePath, string entryPath, Stream destination, string? password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArgumentException.ThrowIfNullOrEmpty(entryPath);
        ArgumentNullException.ThrowIfNull(destination);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var extractor = Open(archivePath, password);
                var wanted = ArchiveEntryInfo.NormalizePath(entryPath);
                var match = Entries(extractor, archivePath).FirstOrDefault(e => !e.Info.IsDirectory && e.Info.Path.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    throw new ArchiveException(ArchiveError.EntryNotFound, $"{entryPath} is not in {Path.GetFileName(archivePath)}.");
                }

                try
                {
                    extractor.ExtractFile(match.Index, new CancellableStream(destination, cancellationToken));
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or ArchiveException))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw Map(ex, password, archivePath);
                }
            },
            cancellationToken);
    }

    private sealed record Listed(int Index, ArchiveEntryInfo Info);

    private static IEnumerable<Listed> Entries(SevenZipExtractor extractor, string archivePath)
    {
        ReadOnlyCollectionOrThrow(extractor, archivePath, out var data);
        foreach (var file in data)
        {
            var name = ArchiveEntryInfo.NormalizePath(string.IsNullOrEmpty(file.FileName) || file.FileName == NamelessEntry
                ? Path.GetFileNameWithoutExtension(archivePath)
                : file.FileName);
            yield return new Listed(
                file.Index,
                new ArchiveEntryInfo(name, file.IsDirectory, file.IsDirectory ? 0 : (long)file.Size, Time(file.LastWriteTime), Time(file.CreationTime), file.Encrypted));
        }
    }

    private static void ReadOnlyCollectionOrThrow(SevenZipExtractor extractor, string archivePath, out System.Collections.ObjectModel.ReadOnlyCollection<ArchiveFileInfo> data)
    {
        try
        {
            data = extractor.ArchiveFileData;
        }
        catch (Exception ex) when (ex is not ArchiveException)
        {
            throw Map(ex, null, archivePath);
        }
    }

    private static DateTimeOffset? Time(DateTime value)
    {
        if (value == DateTime.MinValue || value.Year < 1601)
        {
            return null;
        }

        return new DateTimeOffset(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Local) : value);
    }

    private static SevenZipExtractor Open(string archivePath, string? password)
    {
        EnsureLibrary();
        try
        {
            var extractor = string.IsNullOrEmpty(password) ? new SevenZipExtractor(archivePath) : new SevenZipExtractor(archivePath, password);

            // The archive is only really opened when its list is first asked for; do it here so a bad archive fails at once.
            _ = extractor.ArchiveFileData;
            return extractor;
        }
        catch (Exception ex) when (ex is not ArchiveException)
        {
            throw Map(ex, password, archivePath);
        }
    }

    private static ArchiveException Map(Exception ex, string? password, string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        var text = ex.Message;
        if (ex is DllNotFoundException or BadImageFormatException)
        {
            return new ArchiveException(ArchiveError.Unsupported, "The 7-Zip library could not be loaded, so archives cannot be opened.", ex);
        }

        // "Invalid archive" also says "is it encrypted and a wrong password was provided?" for every damaged file, so only a failure
        // while extracting counts as a password problem. An archive whose list is encrypted cannot be told from a damaged one.
        if (ex is not SevenZipArchiveException && text.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(password)
                ? new ArchiveException(ArchiveError.PasswordRequired, $"{name} is password protected.", ex)
                : new ArchiveException(ArchiveError.WrongPassword, $"The password does not open {name}.", ex);
        }

        if (ex is SevenZipException)
        {
            return new ArchiveException(ArchiveError.Unreadable, $"{name} could not be read as an archive: it is damaged, is not an archive, or its list of files is password protected.", ex);
        }

        return new ArchiveException(ArchiveError.Unreadable, $"{name} could not be read: {text}", ex);
    }

    private static void EnsureLibrary()
    {
        lock (LibraryLock)
        {
            if (_libraryReady)
            {
                return;
            }

            if (_libraryError is not null)
            {
                throw new ArchiveException(ArchiveError.Unsupported, _libraryError);
            }

            var file = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "7zArm64.dll",
                Architecture.X86 => "7z.dll",
                _ => "7z64.dll",
            };
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(path))
            {
                _libraryError = $"{file} was not found beside the program, so archives cannot be opened.";
                throw new ArchiveException(ArchiveError.Unsupported, _libraryError);
            }

            SevenZipBase.SetLibraryPath(path);
            _libraryReady = true;
        }
    }

    /// <summary>A write-only stream that stops 7-Zip when the token is cancelled, because the next write throws.</summary>
    private sealed class CancellableStream(Stream inner, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inner.Write(buffer);
        }
    }
}
