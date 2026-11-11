using System.Globalization;
using Leviathan.Search;
using Microsoft.Data.Sqlite;

namespace Leviathan.Data;

/// <summary>
/// Keeps the <see cref="NameIndex"/> in SQLite between runs (spec §13), so the index of a share is there the moment the app starts and a restart does not
/// mean crawling again. A save replaces the whole index in one transaction (a crash leaves the previous copy); a load that finds anything wrong gives null and the
/// index is built again.
/// </summary>
public sealed class SqliteNameIndexStore : INameIndexStore
{
    private readonly LeviathanDatabase _database;

    /// <summary>Uses <paramref name="database"/>.</summary>
    public SqliteNameIndexStore(LeviathanDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public void Save(IndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM name_index; DELETE FROM name_index_roots;";
            clear.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO name_index (id, parent, name, is_folder, size, modified, attributes) VALUES ($id, $parent, $name, $folder, $size, $modified, $attributes);";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var parent = insert.Parameters.Add("$parent", SqliteType.Integer);
            var name = insert.Parameters.Add("$name", SqliteType.Text);
            var folder = insert.Parameters.Add("$folder", SqliteType.Integer);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            var modified = insert.Parameters.Add("$modified", SqliteType.Integer);
            var attributes = insert.Parameters.Add("$attributes", SqliteType.Integer);
            insert.Prepare();
            foreach (var row in snapshot.Rows)
            {
                id.Value = row.Id;
                parent.Value = row.Parent;
                name.Value = row.Name;
                folder.Value = row.IsFolder ? 1 : 0;
                size.Value = row.Size;
                modified.Value = row.ModifiedTicks;
                attributes.Value = row.Attributes;
                insert.ExecuteNonQuery();
            }
        }

        foreach (var root in snapshot.Roots)
        {
            using var insertRoot = connection.CreateCommand();
            insertRoot.Transaction = transaction;
            insertRoot.CommandText = "INSERT INTO name_index_roots (id, crawled_at) VALUES ($id, $at);";
            insertRoot.Parameters.AddWithValue("$id", root.Id);
            insertRoot.Parameters.AddWithValue("$at", root.CrawledAt is { } when ? when.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            insertRoot.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    public IndexSnapshot? Load()
    {
        try
        {
            using var connection = _database.Open();
            var rows = new List<IndexRow>();
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT id, parent, name, is_folder, size, modified, attributes FROM name_index ORDER BY id;";
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new IndexRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6)));
                }
            }

            var roots = new List<IndexRootState>();
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT id, crawled_at FROM name_index_roots ORDER BY id;";
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    roots.Add(new IndexRootState(reader.GetInt32(0), reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
                }
            }

            return rows.Count == 0 && roots.Count == 0 ? null : new IndexSnapshot(rows, roots);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException or InvalidCastException)
        {
            return null;
        }
    }
}
