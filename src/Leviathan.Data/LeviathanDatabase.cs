using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>
/// The local SQLite database (spec §2.4): WAL mode for crash safety and concurrent readers, a busy timeout so short
/// write contention never surfaces as an error, and ordered schema migrations tracked in <c>schema_version</c>.
/// </summary>
public sealed class LeviathanDatabase
{
    /// <summary>Schema migrations, applied in order. Never edit a shipped migration; add a new one.</summary>
    internal static readonly IReadOnlyList<string> Migrations =
    [
        // 1: folder sizes (spec §12.7)
        """
        CREATE TABLE folder_size (
            location        TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
            own_bytes       INTEGER NOT NULL,
            own_files       INTEGER NOT NULL,
            total_bytes     INTEGER NOT NULL,
            total_files     INTEGER NOT NULL,
            total_folders   INTEGER NOT NULL,
            state           INTEGER NOT NULL,
            computed_at     TEXT NOT NULL,
            folder_modified TEXT NULL
        );
        """,

        // 2: tag index (spec §11.2); filled from Phase 3
        """
        CREATE TABLE tag_index (
            location      TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
            volume_serial INTEGER NULL,
            file_id       TEXT NULL,
            size          INTEGER NULL,
            modified      TEXT NULL,
            tags          TEXT NOT NULL
        );
        CREATE INDEX ix_tag_index_file_id ON tag_index (volume_serial, file_id);
        """,

        // 3: media metadata cache (spec §9.3); filled from Phase 3
        """
        CREATE TABLE media_metadata (
            location   TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
            size       INTEGER NOT NULL,
            modified   TEXT NOT NULL,
            status     INTEGER NOT NULL,
            json       TEXT NULL,
            probed_at  TEXT NOT NULL
        );
        """,

        // 4: folders directly inside a folder, for the Items column (spec §12.7); null until the folder is counted again
        "ALTER TABLE folder_size ADD COLUMN own_folders INTEGER NULL;",

        // 5: the name index behind search (spec §13): every folder and file of the roots the user chose to index, and when each root was last listed completely
        """
        CREATE TABLE name_index (
            id         INTEGER NOT NULL PRIMARY KEY,
            parent     INTEGER NOT NULL,
            name       TEXT NOT NULL,
            is_folder  INTEGER NOT NULL,
            size       INTEGER NOT NULL,
            modified   INTEGER NOT NULL,
            attributes INTEGER NOT NULL
        );
        CREATE TABLE name_index_roots (
            id         INTEGER NOT NULL PRIMARY KEY,
            crawled_at TEXT NULL
        );
        """,

        // 6: the last scan of each location for the space map (spec §14), as one compressed blob per root
        """
        CREATE TABLE space_scan (
            root       TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
            scanned_at TEXT NOT NULL,
            complete   INTEGER NOT NULL,
            data       BLOB NOT NULL
        );
        """,
    ];

    private readonly string _connectionString;

    /// <summary>Opens (and migrates) the database at <paramref name="path"/>.</summary>
    public LeviathanDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();

        Migrate();
    }

    /// <summary>Default location: <c>leviathan.db</c> in the data folder (%LOCALAPPDATA%\Leviathan, or LEVIATHAN_DATA_DIR).</summary>
    public static string DefaultPath => Core.Platform.DataLocation.Combine("leviathan.db");

    /// <summary>Database file path.</summary>
    public string Path { get; }

    /// <summary>Opens a connection with the standard pragmas applied.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>The schema version currently applied.</summary>
    public int SchemaVersion()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Migrate()
    {
        using var connection = Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        int current;
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            current = Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        for (var version = current + 1; version <= Migrations.Count; version++)
        {
            using var transaction = connection.BeginTransaction();
            using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = Migrations[version - 1];
                migrate.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES ($v, $at);";
                record.Parameters.AddWithValue("$v", version);
                record.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
