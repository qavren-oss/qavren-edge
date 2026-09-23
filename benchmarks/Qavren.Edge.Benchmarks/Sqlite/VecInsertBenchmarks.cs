using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Benchmarks.Infrastructure;

namespace Qavren.Edge.Benchmarks.Sqlite;

/// <summary>
/// Bulk vec0 insert: <see cref="Rows"/> 384-dimension vectors in ONE transaction, into an empty
/// table recreated before every iteration, at both <see cref="ChunkSize"/> values (SP2 spec 19
/// item 11). One invocation per iteration, because the table must be empty at the start of each.
/// </summary>
[BenchmarkCategory("Sqlite")]
[InvocationCount(1)]
public class VecInsertBenchmarks
{
    private EdgeHostScope? _host;
    private SqliteConnection? _connection;
    private byte[][] _blobs = [];

    [Params(1_000, 10_000)]
    public int Rows { get; set; }

    [Params(256, 1024)]
    public int ChunkSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _host = EdgeHostScope.Start("sqlite-insert", sqlite: true);
        _connection = _host.Database.OpenConnectionAsync().AsTask().GetAwaiter().GetResult();
        _blobs = SqliteBench.Blobs(Synthetic.UnitVectors(Rows, Synthetic.Seed));
    }

    [IterationSetup]
    public void RecreateTable() =>
        SqliteBench.CreateVecTableAsync(_connection!, ChunkSize).GetAwaiter().GetResult();

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection?.Dispose();
        _host?.Dispose();
    }

    [Benchmark]
    [Throughput("rows", nameof(Rows))]
    public Task<int> InsertBatch() => SqliteBench.InsertVectorsAsync(_host!.Database, _blobs);
}
