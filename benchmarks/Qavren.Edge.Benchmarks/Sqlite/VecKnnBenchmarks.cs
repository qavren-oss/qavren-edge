using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Benchmarks.Infrastructure;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.Benchmarks.Sqlite;

/// <summary>
/// vec0 KNN, <c>k = 10</c>, over <see cref="Rows"/> unit vectors of 384 dimensions, cosine.
/// sqlite-vec's vec0 is a brute-force scan, so the cost is linear in rows; <see cref="ChunkSize"/>
/// is SP2 spec 19 item 11 - 256 is the shipped <c>EdgeVectorStoreOptions.ChunkSize</c>, 1024 is
/// sqlite-vec's own default.
/// </summary>
[BenchmarkCategory("Sqlite")]
public class VecKnnBenchmarks
{
    private EdgeHostScope? _host;
    private SqliteConnection? _connection;
    private float[] _query = [];

    [Params(1_000, 10_000, 100_000)]
    public int Rows { get; set; }

    [Params(256, 1024)]
    public int ChunkSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _host = EdgeHostScope.Start("sqlite-knn", sqlite: true);
        _connection = _host.Database.OpenConnectionAsync().AsTask().GetAwaiter().GetResult();
        SqliteBench.CreateVecTableAsync(_connection, ChunkSize).GetAwaiter().GetResult();
        SqliteBench.InsertVectorsAsync(_host.Database, SqliteBench.Blobs(Synthetic.UnitVectors(Rows, Synthetic.Seed)))
            .GetAwaiter().GetResult();
        _query = Synthetic.UnitVector(new Random(Synthetic.Seed + 1));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection?.Dispose();
        _host?.Dispose();
    }

    /// <summary>One KNN query; returns the hit count so the call cannot be elided.</summary>
    [Benchmark]
    [Throughput("rows", nameof(Rows))]
    public async Task<int> Knn10()
    {
        var hits = await Knn.QueryAsync(_connection!, SqliteBench.VecTableName, _query, k: 10).ConfigureAwait(false);
        return hits.Count;
    }
}
