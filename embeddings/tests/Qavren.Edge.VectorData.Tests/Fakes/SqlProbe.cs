using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.VectorData.Tests.Fakes;

/// <summary>
/// Reads a lane out of the database with no provider code in the path. The RRF assertions need
/// each lane's ORDER independently of the fused query - a fusion table computed from the fused
/// query's own output would assert nothing - and the keyword lane's order is only observable by
/// running FTS5's own <c>ORDER BY rank</c> here.
/// </summary>
internal static class SqlProbe
{
    /// <summary>Runs an arbitrary read against the same <c>IEdgeDatabase</c> the store uses.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="host">The host whose database to read.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="map">Maps one row.</param>
    /// <returns>The rows, in the statement's order.</returns>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        this VectorTestHost host,
        string sql,
        Func<SqliteDataReader, T> map)
    {
        ArgumentNullException.ThrowIfNull(host);

        var token = TestContext.Current.CancellationToken;
        var connection = await host.Database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(sql, map, parameters: null, token).ConfigureAwait(false);
        }
    }

    /// <summary>Every rowid the FTS5 sidecar returns for one MATCH expression, best match first.</summary>
    /// <param name="host">The host whose database to read.</param>
    /// <param name="ftsTable">The FTS5 sidecar's name.</param>
    /// <param name="matchExpression">An FTS5 MATCH expression, already quoted.</param>
    /// <returns>
    /// The rowids in <c>ORDER BY rank</c> order. FTS5's hidden <c>rank</c> column is bm25 * -1, so
    /// ascending IS best-first; a "higher is better, so sort descending" mistake reverses this list
    /// and the fusion built from it.
    /// </returns>
    public static Task<IReadOnlyList<long>> KeywordLaneAsync(
        this VectorTestHost host,
        string ftsTable,
        string matchExpression) =>
        host.QueryAsync(
            $"SELECT f.rowid FROM \"{ftsTable}\" f WHERE f.\"{ftsTable}\" MATCH '{matchExpression.Replace("'", "''", StringComparison.Ordinal)}' ORDER BY f.rank",
            reader => reader.GetInt64(0));

    /// <summary>The rowid of every row of a data table, keyed by the record key.</summary>
    /// <param name="host">The host whose database to read.</param>
    /// <param name="dataTable">The data table.</param>
    /// <param name="rowIdColumn">The rowid alias column.</param>
    /// <param name="keyColumn">The key column.</param>
    /// <returns>Rowid to key.</returns>
    public static async Task<IReadOnlyDictionary<long, string>> KeysByRowIdAsync(
        this VectorTestHost host,
        string dataTable,
        string rowIdColumn,
        string keyColumn)
    {
        var rows = await host.QueryAsync(
            $"SELECT \"{rowIdColumn}\", \"{keyColumn}\" FROM \"{dataTable}\"",
            reader => (RowId: reader.GetInt64(0), Key: reader.GetString(1))).ConfigureAwait(false);

        return rows.ToDictionary(r => r.RowId, r => r.Key);
    }
}
