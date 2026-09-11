using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// Tier 2, the shape sub-project 1's own vector tests already use: 1,000 random vectors, and the
/// store's ordered result compared against a brute-force cosine ranking computed in C#. Repeated at
/// 768-d, which is the only place the wide-vector path is exercised.
/// </summary>
/// <remarks>
/// The vectors are PRE-COMPUTED rather than generated, so nothing about the assertion depends on a
/// generator: the bytes vec0 ranks are the bytes this test wrote, and the expected ranking is
/// computed from the same float arrays. Every vector is unit-length, so vec0's cosine distance is
/// exactly <c>1 - dot</c> and the two rankings are comparable term by term.
/// </remarks>
public sealed class KnnVsBruteForceTests
{
    private const int CorpusSize = 1000;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(384, 1)]
    [InlineData(384, 10)]
    [InlineData(384, 100)]
    [InlineData(768, 1)]
    [InlineData(768, 10)]
    [InlineData(768, 100)]
    public async Task KnnMatchesBruteForceCosine(int dimensions, int k)
    {
        if (dimensions == 384)
        {
            await AssertKnnMatchesBruteForceAsync<Vec384>(dimensions, k);
        }
        else
        {
            await AssertKnnMatchesBruteForceAsync<Vec768>(dimensions, k);
        }
    }

    private static async Task AssertKnnMatchesBruteForceAsync<TRecord>(int dimensions, int k)
        where TRecord : class, IPrecomputedVectorRecord, new()
    {
        using var host = await VectorTestHost.StartAsync().ConfigureAwait(false);
        var collection = new EdgeVectorStoreCollection<string, TRecord>(host.Database, "vectors");
        await collection.EnsureCollectionExistsAsync(Token).ConfigureAwait(false);

        var random = new Random(20260911 + dimensions);
        var corpus = new float[CorpusSize][];
        var records = new TRecord[CorpusSize];
        for (var i = 0; i < CorpusSize; i++)
        {
            corpus[i] = UnitVector(random, dimensions);
            records[i] = new TRecord { Key = "k" + i, Embedding = corpus[i] };
        }

        await collection.UpsertAsync(records, Token).ConfigureAwait(false);

        var query = UnitVector(random, dimensions);

        var expected = Enumerable.Range(0, CorpusSize)
            .Select(i => (Key: records[i].Key, Distance: 1.0 - Dot(query, corpus[i])))
            .OrderBy(x => x.Distance)
            .Take(k)
            .ToArray();

        var hits = await collection
            .SearchAsync(new ReadOnlyMemory<float>(query), top: k, cancellationToken: Token)
            .ToListAsync(Token)
            .ConfigureAwait(false);

        Assert.Equal(k, hits.Count);
        Assert.Equal(expected.Select(e => e.Key), hits.Select(h => h.Record.Key));

        // vec0 computes in float32 and the brute force above in double over the same float32
        // inputs, so the tolerance covers the accumulation order and nothing else.
        for (var i = 0; i < k; i++)
        {
            Assert.Equal(expected[i].Distance, hits[i].Score!.Value, 4);
        }
    }

    private static float[] UnitVector(Random random, int dimensions)
    {
        var vector = new float[dimensions];
        double norm = 0;
        for (var i = 0; i < dimensions; i++)
        {
            vector[i] = (float)(random.NextDouble() - 0.5);
            norm += (double)vector[i] * vector[i];
        }

        var inverse = (float)(1.0 / Math.Sqrt(norm));
        for (var i = 0; i < dimensions; i++)
        {
            vector[i] *= inverse;
        }

        return vector;
    }

    private static double Dot(float[] left, float[] right)
    {
        double total = 0;
        for (var i = 0; i < left.Length; i++)
        {
            total += (double)left[i] * right[i];
        }

        return total;
    }
}
