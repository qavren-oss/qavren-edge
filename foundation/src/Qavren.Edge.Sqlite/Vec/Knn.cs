using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Vec;

public readonly record struct KnnHit(long RowId, float Distance);

/// <summary>
/// Emits the one KNN shape sqlite-vec supports: <c>&lt;vector&gt; MATCH ? AND k = ?</c>.
/// <c>LIMIT n</c> only works on SQLite 3.41+ and is deliberately not used.
/// </summary>
public static class Knn
{
    public static string BuildSql(string table, string? where = null, string vectorColumn = "embedding")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorColumn);

        var sql = $"SELECT rowid, distance FROM \"{table}\" WHERE {vectorColumn} MATCH $query AND k = $k";
        return string.IsNullOrWhiteSpace(where) ? sql : sql + " AND " + where;
    }

    public static async Task<IReadOnlyList<KnnHit>> QueryAsync(
        SqliteConnection connection,
        string table,
        ReadOnlyMemory<float> query,
        int k,
        string? where = null,
        IEnumerable<SqliteParameter>? parameters = null,
        string vectorColumn = "embedding",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = BuildSql(table, where, vectorColumn);
            command.Parameters.Add(new SqliteParameter("$query", VecBlob.From(query.Span)));
            command.Parameters.Add(new SqliteParameter("$k", k));
            if (parameters is not null)
            {
                foreach (var parameter in parameters)
                {
                    command.Parameters.Add(parameter);
                }
            }

            var hits = new List<KnnHit>(k);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    hits.Add(new KnnHit(reader.GetInt64(0), (float)reader.GetDouble(1)));
                }
            }

            return hits;
        }
    }
}
