using System.Text.Json;
using Leviathan.ViewModels.Triage.Learning;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>
/// The facts about decided files, one row per path in <c>triage-facts.db</c>, the facts themselves as JSON. Derived data (it can be
/// collected again from the history and the servers), so a file that cannot be read is deleted and started over, unlike the history.
/// Nothing is opened until the first call (spec §3.1); calls run off the caller's thread; SQLite errors surface as <see cref="IOException"/>.
/// </summary>
public sealed class SqliteFileFactStore(string databasePath) : IFileFactStore, IDisposable
{
    private readonly Lock _openGate = new();
    private string? _connectionString;
    private bool _disposed;

    /// <inheritdoc />
    public Task<FileFacts?> GetAsync(string path, CancellationToken cancellationToken) => Task.Run(() => Guard(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM facts WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() is string json ? Read(json) : null;
    }), cancellationToken);

    /// <inheritdoc />
    public Task SaveAsync(FileFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return Task.Run(() => Guard(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO facts (path, complete, json) VALUES ($path, $complete, $json) ON CONFLICT(path) DO UPDATE SET complete = $complete, json = $json;";
            command.Parameters.AddWithValue("$path", facts.Path);
            command.Parameters.AddWithValue("$complete", facts.Complete ? 1 : 0);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(facts));
            command.ExecuteNonQuery();
            return true;
        }), cancellationToken);
    }

    /// <inheritdoc />
    public Task<(int Complete, int Incomplete)> CountAsync(CancellationToken cancellationToken) => Task.Run(() => Guard(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(complete), 0), COUNT(*) FROM facts;";
        using var reader = command.ExecuteReader();
        reader.Read();
        var complete = (int)reader.GetInt64(0);
        return (complete, (int)reader.GetInt64(1) - complete);
    }), cancellationToken);

    /// <inheritdoc />
    public Task<ISet<string>> CompletePathsAsync(CancellationToken cancellationToken) => Task.Run(() => Guard<ISet<string>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM facts WHERE complete = 1;";
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(reader.GetString(0));
        }

        return paths;
    }), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileFacts>> LoadAllAsync(CancellationToken cancellationToken) => Task.Run(() => Guard<IReadOnlyList<FileFacts>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM facts;";
        var list = new List<FileFacts>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Read(reader.GetString(0)) is { } facts)
            {
                list.Add(facts);
            }
        }

        return list;
    }), cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        if (_connectionString is not null)
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        }
    }

    // A row that no longer reads (an older shape) is skipped: the next backfill collects it again.
    private static FileFacts? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FileFacts>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true, DefaultTimeout = 10 }.ToString();
            try
            {
                Prepare(connectionString);
            }
            catch (SqliteException)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    File.Delete(databasePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Preparing it again then fails and the caller sees an IOException.
                }

                Prepare(connectionString);
            }

            _connectionString = connectionString;
            return connectionString;
        }
    }

    private static void Prepare(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS facts (
                path      TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                complete  INTEGER NOT NULL,
                json      TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SqliteException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }
}
