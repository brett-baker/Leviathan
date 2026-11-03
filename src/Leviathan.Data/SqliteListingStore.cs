using System.IO.Compression;
using System.Text;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Views;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>
/// The saved folder listings on disk (spec §3.1, §12 offline cache): one compressed row per folder in its own database file, so
/// a folder opens at once after a restart and still shows what was there when the share is down. The file holds nothing that
/// cannot be listed again, so a damaged or out-of-date file is simply replaced. Nothing is opened until the first use (the
/// constructor does no I/O: cold start, spec §3.1), every call runs off the calling thread, and failures surface as
/// <see cref="IOException"/> which the cache absorbs. The total size is capped, least recently used folders going first.
/// </summary>
public sealed class SqliteListingStore : IListingStore, IDisposable
{
    /// <summary>The schema version of the file; a file with another one is recreated.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Default cap on the compressed listings held: 256 MB (about 70 folders of 50,000 files at roughly 70 bytes an item).</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>Listings not refreshed for this long are dropped when the file is opened.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(60);

    private const byte FormatVersion = 1;
    private const int MaxEntriesPerListing = 10_000_000;

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private readonly Lock _openGate = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private string? _connectionString;
    private bool _disposed;

    /// <summary>Creates the store; nothing is opened yet.</summary>
    /// <param name="path">The database file, e.g. <c>%LOCALAPPDATA%\Leviathan\listings.db</c>.</param>
    /// <param name="maxBytes">The most compressed listing data to keep.</param>
    /// <param name="time">Clock.</param>
    public SqliteListingStore(string path, long maxBytes = DefaultMaxBytes, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _path = path;
        _maxBytes = maxBytes;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The database file.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public Task<CachedListing?> LoadAsync(ListingKey key, CancellationToken cancellationToken) =>
        Task.Run(() => Guard(() => Load(key, cancellationToken)), cancellationToken);

    /// <inheritdoc />
    public async Task SaveAsync(ListingKey key, CachedListing listing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listing);

        // Compressing is the slow part and needs no lock; only the write is one at a time.
        var blob = await Task.Run(() => Encode(key, listing), cancellationToken).ConfigureAwait(false);
        await WriteAsync(() => Store(key, listing, blob), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task TouchAsync(ListingKey key, DateTimeOffset listedAt, CancellationToken cancellationToken) =>
        WriteAsync(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE listing SET listed_at = $at, last_used = $now WHERE folder = $folder AND hidden = $hidden AND system = $system;";
            Bind(command, key);
            command.Parameters.AddWithValue("$at", listedAt.UtcTicks);
            command.Parameters.AddWithValue("$now", _time.GetUtcNow().UtcTicks);
            command.ExecuteNonQuery();
        }, cancellationToken);

    /// <inheritdoc />
    public Task RemoveAsync(LocationPath folder, bool includeDescendants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return WriteAsync(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            var prefix = folder.Value.TrimEnd('\\') + "\\";
            command.CommandText = includeDescendants
                ? "DELETE FROM listing WHERE folder = $folder OR substr(folder, 1, $length) = $prefix;"
                : "DELETE FROM listing WHERE folder = $folder;";
            command.Parameters.AddWithValue("$folder", folder.Value);
            if (includeDescendants)
            {
                command.Parameters.AddWithValue("$length", prefix.Length);
                command.Parameters.AddWithValue("$prefix", prefix);
            }

            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    /// <summary>Forgets every saved listing (the "clear the folder cache" setting).</summary>
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM listing;";
            command.ExecuteNonQuery();
        }, cancellationToken);

    /// <summary>How many folders and how many compressed bytes are saved.</summary>
    public Task<(int Folders, long Bytes)> UsageAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Guard(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(bytes), 0) FROM listing;";
            using var reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt64(1));
        }), cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _writes.Dispose();
        if (_connectionString is not null)
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        }
    }

    private CachedListing? Load(ListingKey key, CancellationToken cancellationToken)
    {
        byte[] blob;
        DateTimeOffset listedAt;
        FolderKind kind;
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT listed_at, kind, data FROM listing WHERE folder = $folder AND hidden = $hidden AND system = $system;";
            Bind(command, key);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            listedAt = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero);
            kind = (FolderKind)reader.GetInt32(1);
            blob = (byte[])reader[2];
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var entries = Decode(key, blob, cancellationToken);
            _ = Quietly(WriteAsync(() => MarkUsed(key), CancellationToken.None));
            return new CachedListing(entries, listedAt, kind, CacheSource.Disk);
        }
        catch (InvalidDataException)
        {
            // A row that cannot be read is worth nothing: drop it so it is listed afresh and saved again.
            _ = Quietly(RemoveAsync(key.Location, includeDescendants: false, CancellationToken.None));
            throw;
        }
    }

    private void MarkUsed(ListingKey key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE listing SET last_used = $now WHERE folder = $folder AND hidden = $hidden AND system = $system;";
        Bind(command, key);
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().UtcTicks);
        command.ExecuteNonQuery();
    }

    private void Store(ListingKey key, CachedListing listing, byte[] blob)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO listing (folder, hidden, system, listed_at, kind, entries, bytes, last_used, data)
                VALUES ($folder, $hidden, $system, $at, $kind, $entries, $bytes, $now, $data)
                ON CONFLICT(folder, hidden, system) DO UPDATE SET
                    listed_at = excluded.listed_at, kind = excluded.kind, entries = excluded.entries,
                    bytes = excluded.bytes, last_used = excluded.last_used, data = excluded.data;
                """;
            Bind(command, key);
            command.Parameters.AddWithValue("$at", listing.ListedAt.UtcTicks);
            command.Parameters.AddWithValue("$kind", (int)listing.Kind);
            command.Parameters.AddWithValue("$entries", listing.Entries.Count);
            command.Parameters.AddWithValue("$bytes", blob.LongLength);
            command.Parameters.AddWithValue("$now", _time.GetUtcNow().UtcTicks);
            command.Parameters.AddWithValue("$data", blob);
            command.ExecuteNonQuery();
        }

        TrimToSize(connection, transaction);
        transaction.Commit();
    }

    // Least recently used folders go first until the saved data fits the cap again.
    private void TrimToSize(SqliteConnection connection, SqliteTransaction transaction)
    {
        long total;
        using (var sum = connection.CreateCommand())
        {
            sum.Transaction = transaction;
            sum.CommandText = "SELECT COALESCE(SUM(bytes), 0) FROM listing;";
            total = Convert.ToInt64(sum.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (total <= _maxBytes)
        {
            return;
        }

        var doomed = new List<(string Folder, long Hidden, long System)>();
        using (var oldest = connection.CreateCommand())
        {
            oldest.Transaction = transaction;
            oldest.CommandText = "SELECT folder, hidden, system, bytes FROM listing ORDER BY last_used ASC;";
            using var reader = oldest.ExecuteReader();
            while (total > _maxBytes && reader.Read())
            {
                doomed.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
                total -= reader.GetInt64(3);
            }
        }

        foreach (var (folder, hidden, system) in doomed)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM listing WHERE folder = $folder AND hidden = $hidden AND system = $system;";
            delete.Parameters.AddWithValue("$folder", folder);
            delete.Parameters.AddWithValue("$hidden", hidden);
            delete.Parameters.AddWithValue("$system", system);
            delete.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand command, ListingKey key)
    {
        command.Parameters.AddWithValue("$folder", key.Location.Value);
        command.Parameters.AddWithValue("$hidden", key.IncludeHidden ? 1 : 0);
        command.Parameters.AddWithValue("$system", key.IncludeSystem ? 1 : 0);
    }

    // ---- the row format: Brotli over [version][count] then per entry [name][flags][kind][size][created][modified][attributes]
    // ([location when it is not folder + name]) ([identity when present]). A cache, so any change of format bumps FormatVersion
    // and old rows are read as damaged and listed afresh.

    private static byte[] Encode(ListingKey key, CachedListing listing)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(brotli, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            writer.Write7BitEncodedInt(listing.Entries.Count);
            foreach (var entry in listing.Entries)
            {
                var implicitLocation = IsUnderFolder(key.Location, entry);
                var flags = (byte)((implicitLocation ? 0 : 1) | (entry.Identity is null ? 0 : 2));
                writer.Write(entry.Name);
                writer.Write(flags);
                writer.Write((byte)entry.Kind);
                writer.Write7BitEncodedInt64(entry.Size);
                writer.Write(entry.Created.UtcTicks);
                writer.Write(entry.Modified.UtcTicks);
                writer.Write((int)entry.Attributes);
                if (!implicitLocation)
                {
                    writer.Write(entry.Location.Value);
                }

                if (entry.Identity is { } identity)
                {
                    writer.Write(identity.VolumeSerial);
                    writer.Write(identity.FileIdHigh);
                    writer.Write(identity.FileIdLow);
                }
            }
        }

        return output.ToArray();
    }

    private static bool IsUnderFolder(LocationPath folder, FileEntry entry)
    {
        try
        {
            return string.Equals(folder.Combine(entry.Name).Value, entry.Location.Value, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static FileEntry[] Decode(ListingKey key, byte[] blob, CancellationToken cancellationToken)
    {
        try
        {
            using var brotli = new BrotliStream(new MemoryStream(blob), CompressionMode.Decompress);
            using var reader = new BinaryReader(brotli, Encoding.UTF8);
            if (reader.ReadByte() != FormatVersion)
            {
                throw new InvalidDataException("The saved listing is in an older format.");
            }

            var count = reader.Read7BitEncodedInt();
            if (count is < 0 or > MaxEntriesPerListing)
            {
                throw new InvalidDataException("The saved listing has an impossible size.");
            }

            var entries = new FileEntry[count];
            for (var i = 0; i < count; i++)
            {
                if ((i & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var name = reader.ReadString();
                var flags = reader.ReadByte();
                var kind = (FileEntryKind)reader.ReadByte();
                var size = reader.Read7BitEncodedInt64();
                var created = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                var modified = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                var attributes = (FileAttributes)reader.ReadInt32();
                var location = (flags & 1) == 0 ? key.Location.Combine(name) : LocationPath.Parse(reader.ReadString());
                FileIdentity? identity = (flags & 2) == 0 ? null : new FileIdentity(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
                entries[i] = new FileEntry(name, location, kind, size, created, modified, attributes) { Identity = identity };
            }

            return entries;
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or ArgumentException or FormatException or OverflowException)
        {
            throw ex as InvalidDataException ?? new InvalidDataException("The saved listing is damaged.", ex);
        }
    }

    // ---- the file

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(EnsureReady());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private string EnsureReady()
    {
        lock (_openGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connectionString is not null)
            {
                return _connectionString;
            }

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var connectionString = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true, DefaultTimeout = 10 }.ToString();
            try
            {
                Prepare(connectionString);
            }
            catch (SqliteException)
            {
                // Not a database, or damaged: it only held listings, so start again with an empty one.
                SqliteConnection.ClearAllPools();
                DeleteFiles();
                Prepare(connectionString);
            }

            _connectionString = connectionString;
            return connectionString;
        }
    }

    private void Prepare(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (version != SchemaVersion)
        {
            command.CommandText = $"""
                DROP TABLE IF EXISTS listing;
                CREATE TABLE listing (
                    folder    TEXT NOT NULL COLLATE NOCASE,
                    hidden    INTEGER NOT NULL,
                    system    INTEGER NOT NULL,
                    listed_at INTEGER NOT NULL,
                    kind      INTEGER NOT NULL,
                    entries   INTEGER NOT NULL,
                    bytes     INTEGER NOT NULL,
                    last_used INTEGER NOT NULL,
                    data      BLOB NOT NULL,
                    PRIMARY KEY (folder, hidden, system)
                );
                CREATE INDEX ix_listing_last_used ON listing (last_used);
                PRAGMA user_version = {SchemaVersion};
                """;
            command.ExecuteNonQuery();
        }

        command.CommandText = "DELETE FROM listing WHERE listed_at < $oldest;";
        command.Parameters.AddWithValue("$oldest", (_time.GetUtcNow() - Retention).UtcTicks);
        command.ExecuteNonQuery();
    }

    private void DeleteFiles()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // If it cannot be removed, opening it again fails and the caller sees an IOException.
            }
        }
    }

    // Bookkeeping nobody waits for: a failure only means the row keeps its old order or is dropped later.
    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Ignored on purpose.
        }
    }

    private async Task WriteAsync(Action write, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Guard(() =>
            {
                write();
                return true;
            }), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    // SQLite's own exceptions become IOException, which is what the cache absorbs.
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SqliteException ex)
        {
            throw new IOException("The saved folder listings could not be used: " + ex.Message, ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The saved folder listings are closed.", ex);
        }
    }
}
