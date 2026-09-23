using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.Benchmarks.Infrastructure;
using Qavren.Edge.VectorData;

namespace Qavren.Edge.Benchmarks.VectorData;

/// <summary>The attributed record the store maps: a key, an indexed category, full-text body, a vector.</summary>
public sealed class BenchRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    /// <summary>One of ten values, so the filter keeps a tenth of the collection.</summary>
    [VectorStoreData(IsIndexed = true)]
    public string Category { get; set; } = string.Empty;

    [VectorStoreData(IsFullTextIndexed = true)]
    public string Body { get; set; } = string.Empty;

    [VectorStoreVector(Synthetic.Dimensions, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// <c>Qavren.Edge.VectorData</c> over 10 000 records: <c>SearchAsync</c> (vec0 KNN plus the data
/// table join) and <c>HybridSearchAsync</c> (vec0 and FTS5 candidates fused by reciprocal rank),
/// each with and without a LINQ filter on the indexed <see cref="BenchRecord.Category"/>, which the
/// store pushes into SQL rather than applying after the fact. Top 10 throughout. Vectors are
/// supplied, so no embedding generator runs.
/// </summary>
[BenchmarkCategory("VectorData")]
public class VectorSearchBenchmarks
{
    private const int Records = 10_000;
    private const int Top = 10;

    private EdgeHostScope? _host;
    private EdgeVectorStoreCollection<string, BenchRecord>? _collection;
    private ReadOnlyMemory<float> _query;
    private string[] _keywords = [];
    private VectorSearchOptions<BenchRecord> _filtered = new();
    private HybridSearchOptions<BenchRecord> _hybridFiltered = new();

    [GlobalSetup]
    public void Setup()
    {
        _host = EdgeHostScope.Start("vectordata", sqlite: true, (edge, _) => edge.AddVectorStore());
        var store = _host.Services.GetRequiredService<EdgeVectorStore>();
        _collection = store.GetCollection<string, BenchRecord>("bench");
        _collection.EnsureCollectionExistsAsync().GetAwaiter().GetResult();

        var vectors = Synthetic.UnitVectors(Records, Synthetic.Seed);
        var random = new Random(Synthetic.Seed);
        var records = new BenchRecord[Records];
        for (var i = 0; i < Records; i++)
        {
            records[i] = new BenchRecord
            {
                Key = "r" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Category = "c" + (i % 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Body = Synthetic.Sentence(random, 12),
                Embedding = vectors[i],
            };
        }

        _collection.UpsertAsync(records).GetAwaiter().GetResult();

        _query = Synthetic.UnitVector(new Random(Synthetic.Seed + 1));
        _keywords = [Synthetic.SearchTerm, Synthetic.Words[11]];
        _filtered = new VectorSearchOptions<BenchRecord> { Filter = r => r.Category == "c3" };
        _hybridFiltered = new HybridSearchOptions<BenchRecord> { Filter = r => r.Category == "c3" };

        // Every shape must return a full page, or the benchmark is timing an empty result.
        foreach (var (name, count) in new[]
                 {
                     ("Search", Search().GetAwaiter().GetResult()),
                     ("SearchFiltered", SearchFiltered().GetAwaiter().GetResult()),
                     ("Hybrid", Hybrid().GetAwaiter().GetResult()),
                     ("HybridFiltered", HybridFiltered().GetAwaiter().GetResult()),
                 })
        {
            if (count != Top)
            {
                throw new InvalidOperationException($"{name} returned {count} results; {Top} expected.");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _collection?.Dispose();
        _host?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task<int> Search() => Count(_collection!.SearchAsync(_query, Top));

    [Benchmark]
    public Task<int> SearchFiltered() => Count(_collection!.SearchAsync(_query, Top, _filtered));

    [Benchmark]
    public Task<int> Hybrid() => Count(_collection!.HybridSearchAsync(_query, _keywords, Top));

    [Benchmark]
    public Task<int> HybridFiltered() => Count(_collection!.HybridSearchAsync(_query, _keywords, Top, _hybridFiltered));

    private static async Task<int> Count(IAsyncEnumerable<VectorSearchResult<BenchRecord>> results)
    {
        var count = 0;
        await foreach (var _ in results.ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }
}
