using System.Globalization;
using Leviathan.Core.Model;
using Leviathan.ViewModels.Tags;

namespace Leviathan.Data;

/// <summary>
/// The tag index in SQLite (spec §11.2): one row per tagged file in <c>tag_index</c>, found by path or by volume serial plus file ID.
/// Tags are kept as <c>,1,4,</c> so one LIKE finds a tag. A file moved by another program is re-keyed to its new path when it is next
/// seen, because writing a row removes any other row with the same identity.
/// </summary>
public sealed class SqliteTagIndex(LeviathanDatabase database) : ITagIndex
{
    private const string Columns = "location, volume_serial, file_id, size, modified, tags";

    /// <inheritdoc />
    public TagIndexEntry? FindByPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM tag_index WHERE location = $location;";
        command.Parameters.AddWithValue("$location", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <inheritdoc />
    public TagIndexEntry? FindByIdentity(FileIdentity identity)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM tag_index WHERE volume_serial = $serial AND file_id = $id;";
        command.Parameters.AddWithValue("$serial", unchecked((long)identity.VolumeSerial));
        command.Parameters.AddWithValue("$id", FileId(identity));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <inheritdoc />
    public void Put(TagIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Tags.Count == 0)
        {
            Remove(entry.Path);
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (entry.Identity is { } identity)
        {
            using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = "DELETE FROM tag_index WHERE volume_serial = $serial AND file_id = $id AND location <> $location;";
            drop.Parameters.AddWithValue("$serial", unchecked((long)identity.VolumeSerial));
            drop.Parameters.AddWithValue("$id", FileId(identity));
            drop.Parameters.AddWithValue("$location", entry.Path);
            drop.ExecuteNonQuery();
        }

        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO tag_index (location, volume_serial, file_id, size, modified, tags)
            VALUES ($location, $serial, $id, $size, $modified, $tags)
            ON CONFLICT(location) DO UPDATE SET
                volume_serial = excluded.volume_serial,
                file_id = excluded.file_id,
                size = excluded.size,
                modified = excluded.modified,
                tags = excluded.tags;
            """;
        upsert.Parameters.AddWithValue("$location", entry.Path);
        upsert.Parameters.AddWithValue("$serial", entry.Identity is { } i ? unchecked((long)i.VolumeSerial) : DBNull.Value);
        upsert.Parameters.AddWithValue("$id", entry.Identity is { } j ? FileId(j) : DBNull.Value);
        upsert.Parameters.AddWithValue("$size", entry.Size);
        upsert.Parameters.AddWithValue("$modified", entry.Modified.ToString("O", CultureInfo.InvariantCulture));
        upsert.Parameters.AddWithValue("$tags", "," + string.Join(',', entry.Tags.Order()) + ",");
        upsert.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <inheritdoc />
    public void Remove(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tag_index WHERE location = $location;";
        command.Parameters.AddWithValue("$location", path);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> PathsWithTag(int tagId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT location FROM tag_index WHERE tags LIKE $pattern ORDER BY location;";
        command.Parameters.AddWithValue("$pattern", "%," + tagId.ToString(CultureInfo.InvariantCulture) + ",%");
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    private static string FileId(FileIdentity identity) => identity.FileIdHigh.ToString("X16", CultureInfo.InvariantCulture) + identity.FileIdLow.ToString("X16", CultureInfo.InvariantCulture);

    private static TagIndexEntry Read(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        FileIdentity? identity = null;
        if (!reader.IsDBNull(1) && !reader.IsDBNull(2) && reader.GetString(2) is { Length: 32 } id)
        {
            identity = new FileIdentity(unchecked((ulong)reader.GetInt64(1)), ulong.Parse(id[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture), ulong.Parse(id[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        var tags = reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToHashSet();
        var modified = reader.IsDBNull(4) ? DateTimeOffset.UnixEpoch : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new TagIndexEntry(reader.GetString(0), identity, reader.IsDBNull(3) ? 0 : reader.GetInt64(3), modified, tags);
    }
}
