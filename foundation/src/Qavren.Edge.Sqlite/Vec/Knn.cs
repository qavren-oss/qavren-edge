using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Vec;

/// <summary>One result row from a <see cref="Knn.QueryAsync"/> call.</summary>
/// <param name="RowId">The matched row's <c>rowid</c>.</param>
/// <param name="Distance">The vector distance reported by sqlite-vec, in the table's configured metric.</param>
public readonly record struct KnnHit(long RowId, float Distance);

/// <summary>
/// Emits the one KNN shape sqlite-vec supports: <c>&lt;vector&gt; MATCH ? AND k = ?</c>.
/// <c>LIMIT n</c> only works on SQLite 3.41+ and is deliberately not used.
/// </summary>
public static class Knn
{
    /// <summary>Builds the <c>SELECT rowid, distance FROM ... WHERE ... MATCH ? AND k = ?</c> statement, optionally narrowed by <paramref name="where"/>.</summary>
    /// <param name="table">The <c>vec0</c> table to query.</param>
    /// <param name="where">An additional <c>AND</c>-joined predicate (metadata or partition-key filters), or <see langword="null"/> for none.</param>
    /// <param name="vectorColumn">The vector column's name.</param>
    /// <returns>The complete KNN <c>SELECT</c> statement.</returns>
    /// <exception cref="ArgumentException"><paramref name="table"/> or <paramref name="vectorColumn"/> is null, empty, or whitespace.</exception>
    public static string BuildSql(string table, string? where = null, string vectorColumn = "embedding")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorColumn);

        var sql = $"SELECT rowid, distance FROM \"{table}\" WHERE {vectorColumn} MATCH $query AND k = $k";
        return string.IsNullOrWhiteSpace(where) ? sql : sql + " AND " + where;
    }

    /// <summary>Runs a KNN query against a <c>vec0</c> table and returns the matched row ids and distances.</summary>
    /// <param name="connection">The open connection to query on.</param>
    /// <param name="table">The <c>vec0</c> table to query.</param>
    /// <param name="query">The query vector.</param>
    /// <param name="k">The number of nearest neighbours to return.</param>
    /// <param name="where">An additional <c>AND</c>-joined predicate (metadata or partition-key filters), or <see langword="null"/> for none.</param>
    /// <param name="parameters">Parameters bound by <paramref name="where"/>, or <see langword="null"/> for none.</param>
    /// <param name="vectorColumn">The vector column's name.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The matched rows, ordered nearest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is less than 1.</exception>
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
