using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

/// <summary>
/// Spec 13: KNN results must equal brute-force cosine and L2 on 1000 random 384-d vectors for
/// k in {1, 10, 100}. This is the test that would catch a broken blob encoding, a wrong
/// distance_metric token, or an endianness bug on a new platform.
/// </summary>
public class KnnAccuracyTests
{
    private const int Dims = 384;
    private const int Count = 1000;

    [Theory]
    [InlineData(VecMetric.Cosine, 1)]
    [InlineData(VecMetric.Cosine, 10)]
    [InlineData(VecMetric.Cosine, 100)]
    [InlineData(VecMetric.L2, 1)]
    [InlineData(VecMetric.L2, 10)]
    [InlineData(VecMetric.L2, 100)]
    public async Task Vec0_MatchesBruteForce(VecMetric metric, int k)
    {
        var random = new Random(20260910);
        var vectors = new float[Count][];
        for (var i = 0; i < Count; i++)
        {
            var v = new float[Dims];
            for (var d = 0; d < Dims; d++)
            {
                v[d] = (float)((random.NextDouble() * 2.0) - 1.0);
            }

            vectors[i] = v;
        }

        var query = new float[Dims];
        for (var d = 0; d < Dims; d++)
        {
            query[d] = (float)((random.NextDouble() * 2.0) - 1.0);
        }

        var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var hostScope = host.ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var connectionScope = connection.ConfigureAwait(false);

        var table = "vec_" + metric.ToString().ToLowerInvariant();
        await VecTable.CreateAsync(connection, table, Dims, metric, cancellationToken: TestContext.Current.CancellationToken);

        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (transaction.ConfigureAwait(false))
        {
            for (var i = 0; i < Count; i++)
            {
                await connection.ExecuteAsync(
                    $"INSERT INTO \"{table}\"(rowid, embedding) VALUES ($id, $e)",
                    [new SqliteParameter("$id", (long)i), new SqliteParameter("$e", VecBlob.From(vectors[i]))],
                    TestContext.Current.CancellationToken);
            }

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var hits = await Knn.QueryAsync(connection, table, query.AsMemory(), k,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(k, hits.Count);

        var expected = Enumerable.Range(0, Count)
            .Select(i => (Id: (long)i, Distance: metric == VecMetric.Cosine
                ? CosineDistance(query, vectors[i])
                : L2Distance(query, vectors[i])))
            .OrderBy(t => t.Distance)
            .Take(k)
            .ToArray();

        // Ranks can legitimately swap when two distances are within float noise, so compare the
        // returned distances rather than the ids, and compare the id sets after that.
        for (var i = 0; i < k; i++)
        {
            Assert.True(
                Math.Abs(hits[i].Distance - expected[i].Distance) < 1e-4f,
                $"rank {i}: vec0 {hits[i].Distance} vs brute force {expected[i].Distance}");
        }

        Assert.Equal(
            expected.Select(e => e.Id).OrderBy(id => id).ToArray(),
            hits.Select(h => h.RowId).OrderBy(id => id).ToArray());
    }

    private static float L2Distance(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }

        return (float)Math.Sqrt(sum);
    }

    private static float CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return (float)(1.0 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb))));
    }
}
