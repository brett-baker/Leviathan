using System.Globalization;
using System.Text.Json;
using Leviathan.Media;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>
/// The media metadata cache in SQLite (spec §9.3), in the <c>media_metadata</c> table: one row per path, with the size and modified time
/// of the version it describes, a status, and JSON (the parsed metadata for a good file, the reason for an unreadable one). An answer
/// for an older version of a file is never returned, so replacing a file gives it a fresh start.
/// </summary>
public sealed class SqliteMediaMetadataStore : IMediaMetadataStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly LeviathanDatabase _database;

    /// <summary>Creates the store.</summary>
    public SqliteMediaMetadataStore(LeviathanDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public MediaMetadataEntry? Get(MediaFileKey key)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT size, modified, status, json, probed_at FROM media_metadata WHERE location = $location;";
        command.Parameters.AddWithValue("$location", key.Path);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var stored = new MediaFileKey(key.Path, reader.GetInt64(0), ParseTime(reader.GetString(1)).UtcDateTime);
        if (!stored.Matches(key))
        {
            return null;
        }

        var state = (MetadataState)reader.GetInt32(2);
        var json = reader.IsDBNull(3) ? null : reader.GetString(3);
        var at = ParseTime(reader.GetString(4));
        return state switch
        {
            MetadataState.Ok => ReadInfo(json) is { } info ? new MediaMetadataEntry(stored, state, info, null, at) : null,
            _ => new MediaMetadataEntry(stored, state, null, json, at),
        };
    }

    /// <inheritdoc />
    public void Put(MediaMetadataEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Write(entry.Key, entry.State, entry.State == MetadataState.Ok ? JsonSerializer.Serialize(entry.Info, Json) : entry.Message, entry.At);
    }

    /// <inheritdoc />
    public void MarkProbing(MediaFileKey key, DateTimeOffset at) => Write(key, MetadataState.Probing, null, at);

    /// <inheritdoc />
    public int QuarantineInterrupted(DateTimeOffset at, string reason)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media_metadata SET status = $unreadable, json = $reason, probed_at = $at WHERE status = $probing;";
        command.Parameters.AddWithValue("$unreadable", (int)MetadataState.Unreadable);
        command.Parameters.AddWithValue("$probing", (int)MetadataState.Probing);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$at", at.ToString("o", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public bool Forget(string path)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_metadata WHERE location = $location;";
        command.Parameters.AddWithValue("$location", path);
        return command.ExecuteNonQuery() > 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaMetadataEntry> Unreadable()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT location, size, modified, json, probed_at FROM media_metadata WHERE status = $unreadable ORDER BY probed_at DESC;";
        command.Parameters.AddWithValue("$unreadable", (int)MetadataState.Unreadable);
        using var reader = command.ExecuteReader();
        var list = new List<MediaMetadataEntry>();
        while (reader.Read())
        {
            var key = new MediaFileKey(reader.GetString(0), reader.GetInt64(1), ParseTime(reader.GetString(2)).UtcDateTime);
            list.Add(new MediaMetadataEntry(key, MetadataState.Unreadable, null, reader.IsDBNull(3) ? null : reader.GetString(3), ParseTime(reader.GetString(4))));
        }

        return list;
    }

    private void Write(MediaFileKey key, MetadataState state, string? json, DateTimeOffset at)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_metadata (location, size, modified, status, json, probed_at)
            VALUES ($location, $size, $modified, $status, $json, $at)
            ON CONFLICT(location) DO UPDATE SET
                size = excluded.size,
                modified = excluded.modified,
                status = excluded.status,
                json = excluded.json,
                probed_at = excluded.probed_at;
            """;
        command.Parameters.AddWithValue("$location", key.Path);
        command.Parameters.AddWithValue("$size", key.Size);
        command.Parameters.AddWithValue("$modified", key.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", (int)state);
        command.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", at.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    // A row that cannot be read back (written by another version) is treated as no answer, so the file is simply probed again.
    private static MediaInfo? ReadInfo(string? json)
    {
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<MediaInfo>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset ParseTime(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
