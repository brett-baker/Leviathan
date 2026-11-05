using System.Globalization;
using Leviathan.ViewModels.Activity;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>
/// The activity log on disk (spec §16.1): one row per entry in its own <c>activity.db</c>, so the UI process owns it and never
/// contends with the worker's database. Nothing is opened until the first write or read (cold start, spec §3.1), calls run off the
/// caller's thread, a damaged file is replaced (a history is not worth a failed start), and SQLite errors surface as
/// <see cref="IOException"/>, which the log absorbs.
/// </summary>
public sealed class SqliteActivityStore : IActivityStore, IDisposable
{
    /// <summary>The schema version of the file; a file with another one is recreated.</summary>
    public const int SchemaVersion = 2;

    private readonly string _path;
    private readonly Lock _openGate = new();
    private string? _connectionString;
    private bool _disposed;

    /// <summary>Creates the store; nothing is opened yet.</summary>
    /// <param name="path">The database file, e.g. <c>%LOCALAPPDATA%\Leviathan\activity.db</c>.</param>
    public SqliteActivityStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public Task AppendAsync(IReadOnlyList<ActivityEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Task.Run(() => Guard(() =>
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO activity (time, session, kind, command, summary, target, location, is_error, job)
                VALUES ($time, $session, $kind, $command, $summary, $target, $location, $error, $job);
                """;
            var job = command.Parameters.Add("$job", SqliteType.Text);
            var time = command.Parameters.Add("$time", SqliteType.Integer);
            var session = command.Parameters.Add("$session", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Integer);
            var name = command.Parameters.Add("$command", SqliteType.Text);
            var summary = command.Parameters.Add("$summary", SqliteType.Text);
            var target = command.Parameters.Add("$target", SqliteType.Text);
            var location = command.Parameters.Add("$location", SqliteType.Text);
            var error = command.Parameters.Add("$error", SqliteType.Integer);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                time.Value = entry.Time.UtcTicks;
                session.Value = entry.Session.ToString("N", CultureInfo.InvariantCulture);
                kind.Value = (int)entry.Kind;
                name.Value = entry.Command;
                summary.Value = entry.Summary;
                target.Value = (object?)entry.Target ?? DBNull.Value;
                location.Value = (object?)entry.Location ?? DBNull.Value;
                error.Value = entry.IsError ? 1 : 0;
                job.Value = entry.JobId is { } id ? id.ToString("N", CultureInfo.InvariantCulture) : DBNull.Value;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        }), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ActivityEntry>> LoadAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken) =>
        Task.Run(() => Guard<IReadOnlyList<ActivityEntry>>(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT time, session, kind, command, summary, target, location, is_error, job
                FROM activity WHERE time >= $since ORDER BY time DESC, id DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$since", since.UtcTicks);
            command.Parameters.AddWithValue("$limit", limit);
            var list = new List<ActivityEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(new ActivityEntry(
                    0,
                    new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                    Guid.TryParseExact(reader.GetString(1), "N", out var id) ? id : Guid.Empty,
                    Enum.IsDefined((ActivityKind)reader.GetInt32(2)) ? (ActivityKind)reader.GetInt32(2) : ActivityKind.Other,
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7) != 0,
                    !reader.IsDBNull(8) && Guid.TryParseExact(reader.GetString(8), "N", out var jobId) ? jobId : null));
            }

            return list;
        }), cancellationToken);

    /// <inheritdoc />
    public Task PruneAsync(DateTimeOffset before, CancellationToken cancellationToken) =>
        Run("DELETE FROM activity WHERE time < $before;", c => c.Parameters.AddWithValue("$before", before.UtcTicks), cancellationToken);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken) => Run("DELETE FROM activity;", null, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        if (_connectionString is not null)
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        }
    }

    private Task<bool> Run(string sql, Action<SqliteCommand>? bind, CancellationToken cancellationToken) =>
        Task.Run(() => Guard(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command);
            command.ExecuteNonQuery();
            return true;
        }), cancellationToken);

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

            var directory = Path.GetDirectoryName(_path);
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
                // Not a database, or damaged: start again with an empty history.
                SqliteConnection.ClearAllPools();
                foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Opening it again then fails and the caller sees an IOException.
                    }
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
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version == 1)
        {
            // Version 1 lacked the job column; the history is worth keeping, so add the column instead of starting again.
            command.CommandText = $"ALTER TABLE activity ADD COLUMN job TEXT NULL; PRAGMA user_version = {SchemaVersion};";
            command.ExecuteNonQuery();
        }
        else if (version != SchemaVersion)
        {
            command.CommandText = $"""
                DROP TABLE IF EXISTS activity;
                CREATE TABLE activity (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    time      INTEGER NOT NULL,
                    session   TEXT NOT NULL,
                    kind      INTEGER NOT NULL,
                    command   TEXT NOT NULL,
                    summary   TEXT NOT NULL,
                    target    TEXT NULL,
                    location  TEXT NULL,
                    is_error  INTEGER NOT NULL,
                    job       TEXT NULL
                );
                CREATE INDEX ix_activity_time ON activity (time);
                PRAGMA user_version = {SchemaVersion};
                """;
            command.ExecuteNonQuery();
        }
    }

    // SQLite's own exceptions become IOException, which is what the activity log absorbs.
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SqliteException ex)
        {
            throw new IOException("The activity history could not be used: " + ex.Message, ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The activity history is closed.", ex);
        }
    }
}
