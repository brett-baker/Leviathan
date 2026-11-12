using System.Globalization;
using Leviathan.Media;

namespace Leviathan.Data;

/// <summary>
/// The perceptual signatures in SQLite (spec §15.2), in the <c>media_signature</c> table: one row per path with the size and modified
/// time of the version it describes, so a replaced file is hashed again. The hashes are 16-digit hex values joined by commas.
/// </summary>
public sealed class SqliteMediaSignatureStore(LeviathanDatabase database) : IMediaSignatureStore
{
    private readonly LeviathanDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public MediaSignature? Get(MediaFileKey key)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT size, modified, kind, duration, hashes FROM media_signature WHERE location = $location;";
        command.Parameters.AddWithValue("$location", key.Path);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var stored = new MediaFileKey(key.Path, reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime);
        if (!stored.Matches(key))
        {
            return null;
        }

        // A row that cannot be read back (written by another version) is treated as no answer, so the file is hashed again.
        var hashes = new List<ulong>();
        foreach (var part in reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ulong.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
            {
                return null;
            }

            hashes.Add(hash);
        }

        return hashes.Count == 0 ? null : new MediaSignature((SignatureKind)reader.GetInt32(2), hashes, reader.GetDouble(3));
    }

    /// <inheritdoc />
    public void Put(MediaFileKey key, MediaSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_signature (location, size, modified, kind, duration, hashes)
            VALUES ($location, $size, $modified, $kind, $duration, $hashes)
            ON CONFLICT(location) DO UPDATE SET
                size = excluded.size,
                modified = excluded.modified,
                kind = excluded.kind,
                duration = excluded.duration,
                hashes = excluded.hashes;
            """;
        command.Parameters.AddWithValue("$location", key.Path);
        command.Parameters.AddWithValue("$size", key.Size);
        command.Parameters.AddWithValue("$modified", key.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$kind", (int)signature.Kind);
        command.Parameters.AddWithValue("$duration", signature.DurationSeconds);
        command.Parameters.AddWithValue("$hashes", string.Join(',', signature.Hashes.Select(h => h.ToString("x16", CultureInfo.InvariantCulture))));
        command.ExecuteNonQuery();
    }
}
