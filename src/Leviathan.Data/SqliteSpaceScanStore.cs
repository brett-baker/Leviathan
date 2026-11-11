using System.Globalization;
using Leviathan.SpaceMap;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>
/// Keeps the last scan of each location for the space map (spec §14 "Caching") in SQLite: one compressed row per root, replaced as a whole, so a map of a
/// share opens at once and a scan that was stopped or killed can continue. A copy that cannot be read is treated as missing.
/// </summary>
public sealed class SqliteSpaceScanStore : ISpaceScanStore
{
    private readonly LeviathanDatabase _database;

    /// <summary>Uses <paramref name="database"/>.</summary>
    public SqliteSpaceScanStore(LeviathanDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public void Save(SpaceScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var data = SpaceScanCodec.Encode(snapshot);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO space_scan (root, scanned_at, complete, data) VALUES ($root, $at, $complete, $data) ON CONFLICT(root) DO UPDATE SET scanned_at = $at, complete = $complete, data = $data;";
        command.Parameters.AddWithValue("$root", snapshot.Root);
        command.Parameters.AddWithValue("$at", snapshot.ScannedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$complete", snapshot.IsComplete ? 1 : 0);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public SpaceScanSnapshot? Load(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM space_scan WHERE root = $root;";
            command.Parameters.AddWithValue("$root", root);
            return command.ExecuteScalar() is byte[] data ? SpaceScanCodec.Decode(data) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Delete(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM space_scan WHERE root = $root;";
        command.Parameters.AddWithValue("$root", root);
        command.ExecuteNonQuery();
    }
}
