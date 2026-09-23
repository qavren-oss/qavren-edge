using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Benchmarks.Infrastructure;
using Qavren.Edge.Sqlite.Fts;

namespace Qavren.Edge.Benchmarks.Sqlite;

/// <summary>
/// FTS5 <c>MATCH</c> for one term, top 10 by <c>bm25</c>, over <see cref="Rows"/> twelve-word rows
/// drawn uniformly from 2 000 pseudo-words (about 0.6% of rows match). The table is created with
/// <see cref="FtsTable.BuildCreateSql(string, IReadOnlyList{string}, FtsTokenizer, string?)"/> and
/// the store's default tokenizer, <c>unicode61</c>.
/// </summary>
[BenchmarkCategory("Sqlite")]
public class FtsMatchBenchmarks
{
    private EdgeHostScope? _host;
    private SqliteConnection? _connection;
    private SqliteCommand? _query;

    [Params(1_000, 10_000, 100_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _host = EdgeHostScope.Start("sqlite-fts", sqlite: true);
        _connection = _host.Database.OpenConnectionAsync().AsTask().GetAwaiter().GetResult();
        SqliteBench.ExecuteAsync(_connection, FtsTable.BuildCreateSql(SqliteBench.FtsTableName, ["body"]))
            .GetAwaiter().GetResult();

        var random = new Random(Synthetic.Seed);
        var bodies = new string[Rows];
        for (var i = 0; i < Rows; i++)
        {
            bodies[i] = Synthetic.Sentence(random, 12);
        }

        _host.Database.ExecuteInTransactionAsync(async (connection, transaction, ct) =>
        {
            var insert = connection.CreateCommand();
            await using (insert.ConfigureAwait(false))
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO \"" + SqliteBench.FtsTableName + "\"(rowid, body) VALUES ($id, $body)";
                var id = insert.Parameters.Add("$id", SqliteType.Integer);
                var body = insert.Parameters.Add("$body", SqliteType.Text);
                for (var i = 0; i < bodies.Length; i++)
                {
                    id.Value = i + 1L;
                    body.Value = bodies[i];
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            return bodies.Length;
        }).GetAwaiter().GetResult();

        _query = _connection.CreateCommand();
        _query.CommandText =
            "SELECT rowid FROM \"" + SqliteBench.FtsTableName + "\" WHERE \"" + SqliteBench.FtsTableName +
            "\" MATCH $q ORDER BY bm25(\"" + SqliteBench.FtsTableName + "\") LIMIT 10";
        _query.Parameters.AddWithValue("$q", Synthetic.SearchTerm);
        _query.Prepare();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _query?.Dispose();
        _connection?.Dispose();
        _host?.Dispose();
    }

    /// <summary>One MATCH query; returns the hit count so the call cannot be elided.</summary>
    // No rows/s column: an index lookup does not touch every row, so rows per second would flatter it.
    [Benchmark]
    public async Task<int> MatchTop10()
    {
        var hits = 0;
        var reader = await _query!.ExecuteReaderAsync().ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                hits++;
            }
        }

        return hits;
    }
}
