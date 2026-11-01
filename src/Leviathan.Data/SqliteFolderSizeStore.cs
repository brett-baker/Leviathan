using System.Globalization;
using Leviathan.Core.Paths;
using Leviathan.Engine.FolderSizes;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>Folder sizes persisted in SQLite (spec §12.7).</summary>
public sealed class SqliteFolderSizeStore : IFolderSizeStore
{
    private readonly LeviathanDatabase _database;

    /// <summary>Creates the store.</summary>
    public SqliteFolderSizeStore(LeviathanDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public FolderSize? Get(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT own_bytes, own_files, total_bytes, total_files, total_folders, state, computed_at, folder_modified
            FROM folder_size WHERE location = $location;
            """;
        command.Parameters.AddWithValue("$location", location.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new FolderSize(
            location,
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            (FolderSizeState)reader.GetInt32(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// <inheritdoc />
    public void Upsert(FolderSize size)
    {
        ArgumentNullException.ThrowIfNull(size);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO folder_size (location, own_bytes, own_files, total_bytes, total_files, total_folders, state, computed_at, folder_modified)
            VALUES ($location, $ownBytes, $ownFiles, $totalBytes, $totalFiles, $totalFolders, $state, $computedAt, $folderModified)
            ON CONFLICT(location) DO UPDATE SET
                own_bytes = excluded.own_bytes,
                own_files = excluded.own_files,
                total_bytes = excluded.total_bytes,
                total_files = excluded.total_files,
                total_folders = excluded.total_folders,
                state = excluded.state,
                computed_at = excluded.computed_at,
                folder_modified = excluded.folder_modified;
            """;
        command.Parameters.AddWithValue("$location", size.Location.Value);
        command.Parameters.AddWithValue("$ownBytes", size.OwnBytes);
        command.Parameters.AddWithValue("$ownFiles", size.OwnFiles);
        command.Parameters.AddWithValue("$totalBytes", size.TotalBytes);
        command.Parameters.AddWithValue("$totalFiles", size.TotalFiles);
        command.Parameters.AddWithValue("$totalFolders", size.TotalFolders);
        command.Parameters.AddWithValue("$state", (int)size.State);
        command.Parameters.AddWithValue("$computedAt", size.ComputedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$folderModified", (object?)size.FolderModified?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void RemoveTree(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM folder_size WHERE location = $location OR location LIKE $prefix ESCAPE '^';";
        command.Parameters.AddWithValue("$location", location.Value);
        command.Parameters.AddWithValue("$prefix", EscapeLike(ChildPrefix(location)) + "%");
        command.ExecuteNonQuery();
    }

    private static string ChildPrefix(LocationPath location) =>
        location.Kind is LocationKind.Drive or LocationKind.Unc or LocationKind.Shell
            ? location.Value.TrimEnd('\\') + "\\"
            : location.Value.TrimEnd('/') + "/";

    private static string EscapeLike(string value) =>
        value.Replace("^", "^^", StringComparison.Ordinal).Replace("%", "^%", StringComparison.Ordinal).Replace("_", "^_", StringComparison.Ordinal);
}
