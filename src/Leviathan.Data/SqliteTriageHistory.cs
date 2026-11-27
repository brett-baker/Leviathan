using System.Globalization;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Triage.Learning;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>
/// The triage decisions kept for the learning, one row per decision in its own <c>triage-history.db</c> (the UI process owns it, like the
/// activity log). Append-only, and these rows are the training data, so a file that cannot be read is moved aside, never deleted.
/// Nothing is opened until the first call (cold start, spec §3.1); calls run off the caller's thread; SQLite errors surface as
/// <see cref="IOException"/>, which the recorder absorbs.
/// </summary>
public sealed class SqliteTriageHistory : ITriageHistory, IDisposable
{
    private readonly string _path;
    private readonly Lock _openGate = new();
    private string? _connectionString;
    private bool _disposed;

    /// <summary>Creates the store; nothing is opened yet.</summary>
    public SqliteTriageHistory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public Task AppendAsync(TriageDecisionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Task.Run(() => Guard(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO decision (time, path, decision, reason, size, modified)
                VALUES ($time, $path, $decision, $reason, $size, $modified);
                """;
            command.Parameters.AddWithValue("$time", record.Time.UtcTicks);
            command.Parameters.AddWithValue("$path", record.Path);
            command.Parameters.AddWithValue("$decision", (int)record.Decision);
            command.Parameters.AddWithValue("$reason", (object?)record.ReasonId ?? DBNull.Value);
            command.Parameters.AddWithValue("$size", record.Size);
            command.Parameters.AddWithValue("$modified", record.ModifiedTicks);
            command.ExecuteNonQuery();
            return true;
        }), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TriageDecisionRecord>> LoadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Guard<IReadOnlyList<TriageDecisionRecord>>(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT time, path, decision, reason, size, modified FROM decision ORDER BY id;";
            var list = new List<TriageDecisionRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = reader.GetInt32(2);
                list.Add(new TriageDecisionRecord(
                    new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                    reader.GetString(1),
                    Enum.IsDefined((TriageDecision)decision) ? (TriageDecision)decision : TriageDecision.None,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5)));
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
                // Damaged: set it aside (the decisions in it may still be recoverable) and start a new one.
                SqliteConnection.ClearAllPools();
                try
                {
                    File.Move(_path, $"{_path}.damaged-{DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)}");
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
            CREATE TABLE IF NOT EXISTS decision (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                time      INTEGER NOT NULL,
                path      TEXT NOT NULL,
                decision  INTEGER NOT NULL,
                reason    TEXT NULL,
                size      INTEGER NOT NULL,
                modified  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_decision_path ON decision (path COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
    }

    // SQLite's own exceptions become IOException, which the recorder absorbs.
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
