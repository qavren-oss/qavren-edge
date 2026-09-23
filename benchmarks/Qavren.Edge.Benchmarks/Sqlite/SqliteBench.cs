using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.Benchmarks.Sqlite;

/// <summary>SQL the three SQLite benchmark classes share.</summary>
internal static class SqliteBench
{
    public const string VecTableName = "bench_vec";

    public const string FtsTableName = "bench_fts";

    /// <summary>Creates the vec0 table with the given <c>chunk_size</c>, 384 dims, cosine.</summary>
    public static async Task CreateVecTableAsync(SqliteConnection connection, int chunkSize)
    {
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS \"" + VecTableName + "\"").ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            VecTable.BuildCreateSql(VecTableName, Infrastructure.Synthetic.Dimensions, VecMetric.Cosine, chunkSize: chunkSize))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts every vector as rowid 1..n inside ONE transaction, through the public
    /// <see cref="IEdgeDatabase.ExecuteInTransactionAsync{T}"/>, with one prepared command reused
    /// for every row - the shape a consumer's bulk load takes.
    /// </summary>
    public static Task<int> InsertVectorsAsync(IEdgeDatabase database, byte[][] blobs) =>
        database.ExecuteInTransactionAsync(async (connection, transaction, ct) =>
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO \"" + VecTableName + "\"(rowid, embedding) VALUES ($id, $v)";
                var id = command.Parameters.Add("$id", SqliteType.Integer);
                var vector = command.Parameters.Add("$v", SqliteType.Blob);
                command.Prepare();

                for (var i = 0; i < blobs.Length; i++)
                {
                    id.Value = i + 1L;
                    vector.Value = blobs[i];
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            return blobs.Length;
        });

    /// <summary>The vectors as the float32 blobs vec0 stores.</summary>
    public static byte[][] Blobs(float[][] vectors)
    {
        var blobs = new byte[vectors.Length][];
        for (var i = 0; i < vectors.Length; i++)
        {
            blobs[i] = VecBlob.From(vectors[i]);
        }

        return blobs;
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
