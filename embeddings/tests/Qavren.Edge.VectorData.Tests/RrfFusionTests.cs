using Microsoft.Extensions.VectorData;
using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// Reciprocal rank fusion, against a fusion table computed here rather than read back out of the
/// fused query. The vector lane is hand-built: every record carries a PRE-COMPUTED unit vector whose
/// cosine similarity to the query is a number chosen in this file, so the vector lane's order is
/// <c>f, c, b, d, a, e</c> by construction and not by whatever a hash-seeded generator happened to
/// produce this process. The keyword lane's order is read from FTS5's own <c>ORDER BY rank</c>,
/// which is the only place bm25 is knowable. The expected score is then
/// <c>wKeyword / (rrfK + keywordRank) + wVector / (rrfK + vectorRank)</c> - the formula the emitted
/// SQL spells out - and the corpus is built so the two lanes DISAGREE, which is the only arrangement
/// in which a fusion bug shows up at all.
/// </summary>
/// <remarks>
/// This is also where the bm25 sign trap is caught. FTS5's hidden <c>rank</c> column is the standard
/// bm25 score multiplied by -1, so a better match is numerically LOWER and ascending order is
/// best-first. An implementation that "sorts descending because higher is better" produces a
/// plausible-looking, exactly inverted keyword lane, and the only thing that notices is a fusion
/// table built from the lane's real order.
/// </remarks>
public sealed class RrfFusionTests
{
    private const string Keyword = "roof";

    /// <summary>The query vector. Every corpus vector's first component IS its cosine against it.</summary>
    private static readonly float[] Query = [1f, 0f, 0f, 0f];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Six documents. Three carry the keyword with clearly different lengths and term frequencies,
    /// three do not carry it at all - so the keyword lane holds three rows and the vector lane holds
    /// all six, and the cosines are chosen so the keyword rows are NOT the nearest ones.
    /// </summary>
    private static FtsVec[] Corpus() =>
    [
        new() { Key = "a", Body = "roof roof roof roof shingles", Embedding = AtCosine(0.50f) },
        new() { Key = "b", Body = "roof tiles and flashing over the porch", Embedding = AtCosine(0.90f) },
        new() { Key = "c", Body = "gutter and downspout cleaning", Embedding = AtCosine(0.95f) },
        new() { Key = "d", Body = "a slow leak in the upstairs ceiling", Embedding = AtCosine(0.60f) },
        new() { Key = "e", Body = "roof", Embedding = AtCosine(0.20f) },
        new() { Key = "f", Body = "basement flooding after the storm", Embedding = AtCosine(0.99f) },
    ];

    [Fact]
    public async Task TheVectorLaneIsTheHandChosenCosineOrder()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host);
        await collection.EnsureCollectionExistsAsync(Token);
        await collection.UpsertAsync(Corpus(), Token);

        var lane = await collection
            .SearchAsync(new ReadOnlyMemory<float>(Query), top: 6, cancellationToken: Token)
            .ToListAsync(Token);

        Assert.Equal(["f", "c", "b", "d", "a", "e"], lane.Select(h => h.Record.Key));
    }

    [Fact]
    public async Task TheFusedOrderMatchesAHandComputedFusionTable()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host);
        await collection.EnsureCollectionExistsAsync(Token);
        await collection.UpsertAsync(Corpus(), Token);

        var expected = await ExpectedFusionAsync(host, collection, rrfK: 60, vectorWeight: 1.0, keywordWeight: 1.0);

        var hits = await collection
            .HybridSearchAsync(new ReadOnlyMemory<float>(Query), [Keyword], top: 3, cancellationToken: Token)
            .ToListAsync(Token);

        Assert.Equal(3, hits.Count);
        Assert.Equal(expected.Take(3).Select(e => e.Key), hits.Select(h => h.Record.Key));
        for (var i = 0; i < hits.Count; i++)
        {
            Assert.Equal(expected[i].Score, hits[i].Score!.Value, 12);
        }

        // The two lanes really do disagree, or the assertion above proves nothing: the vector lane's
        // own top three is f, c, b and at least one keyword-only row outranks one of them once fused.
        Assert.NotEqual<IEnumerable<string>>(["f", "c", "b"], hits.Select(h => h.Record.Key).ToArray());
    }

    [Fact]
    public async Task RrfKMovesEveryFusedScoreAndBothValuesMatchTheFormula()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host);
        await collection.EnsureCollectionExistsAsync(Token);
        await collection.UpsertAsync(Corpus(), Token);

        var wide = await Fused(collection, new EdgeHybridSearchOptions<FtsVec> { RrfK = 60 });
        var tight = await Fused(collection, new EdgeHybridSearchOptions<FtsVec> { RrfK = 2 });

        var expectedWide = await ExpectedFusionAsync(host, collection, 60, vectorWeight: 1.0, keywordWeight: 1.0);
        var expectedTight = await ExpectedFusionAsync(host, collection, 2, vectorWeight: 1.0, keywordWeight: 1.0);

        Assert.Equal(expectedWide.Take(3).Select(e => e.Key), wide.Select(h => h.Record.Key));
        Assert.Equal(expectedTight.Take(3).Select(e => e.Key), tight.Select(h => h.Record.Key));

        // Not a formality: an implementation that ignored RrfK would return the same numbers twice.
        Assert.All(
            wide.Zip(tight),
            pair => Assert.NotEqual(pair.First.Score!.Value, pair.Second.Score!.Value, 6));
    }

    [Fact]
    public async Task ZeroingTheVectorWeightLeavesTheKeywordLanesOwnOrder()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host);
        await collection.EnsureCollectionExistsAsync(Token);
        await collection.UpsertAsync(Corpus(), Token);

        var keywordLane = await KeywordLaneKeysAsync(host, collection);

        var hits = await Fused(
            collection,
            new EdgeHybridSearchOptions<FtsVec> { VectorWeight = 0.0, KeywordWeight = 1.0 });

        Assert.Equal(keywordLane, hits.Select(h => h.Record.Key));
    }

    [Fact]
    public async Task ZeroingTheKeywordWeightLeavesTheVectorLanesOwnOrder()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host);
        await collection.EnsureCollectionExistsAsync(Token);
        await collection.UpsertAsync(Corpus(), Token);

        var hits = await Fused(
            collection,
            new EdgeHybridSearchOptions<FtsVec> { VectorWeight = 1.0, KeywordWeight = 0.0 });

        Assert.Equal(["f", "c", "b"], hits.Select(h => h.Record.Key));
    }

    [Fact]
    public async Task TheKeywordLaneIsRankedAscendingBecauseFts5sRankIsBm25TimesMinusOne()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host);
        await collection.EnsureCollectionExistsAsync(Token);
        await collection.UpsertAsync(Corpus(), Token);

        var fts = collection.Schema.FullTextTable!;
        var ranks = await host.QueryAsync(
            $"SELECT f.rank FROM \"{fts}\" f WHERE f.\"{fts}\" MATCH '\"{Keyword}\"' ORDER BY f.rank",
            reader => reader.GetDouble(0));

        // bm25 * -1: every rank is negative, and the best match is the most negative one.
        Assert.Equal(3, ranks.Count);
        Assert.All(ranks, r => Assert.True(r < 0, "FTS5 rank is bm25 * -1 and must be negative."));
        Assert.Equal(ranks.OrderBy(r => r), ranks);

        // And the keyword-only fusion returns the rows in exactly that order, which is what a
        // "descending because higher is better" mistake would invert.
        var hits = await Fused(
            collection,
            new EdgeHybridSearchOptions<FtsVec> { VectorWeight = 0.0, KeywordWeight = 1.0 });

        Assert.Equal(await KeywordLaneKeysAsync(host, collection), hits.Select(h => h.Record.Key));
    }

    [Fact]
    public async Task HybridScoreIsASimilarityWhereHigherIsBetter_TheOppositeOfSearchAsyncsDistance()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = Collection(host);
        await collection.EnsureCollectionExistsAsync(Token);
        await collection.UpsertAsync(Corpus(), Token);

        var hybrid = await collection
            .HybridSearchAsync(new ReadOnlyMemory<float>(Query), [Keyword], top: 6, cancellationToken: Token)
            .ToListAsync(Token);
        var vector = await collection
            .SearchAsync(new ReadOnlyMemory<float>(Query), top: 6, cancellationToken: Token)
            .ToListAsync(Token);

        var fused = hybrid.Select(h => h.Score!.Value).ToArray();
        var distances = vector.Select(h => h.Score!.Value).ToArray();

        Assert.Equal(fused.OrderByDescending(s => s), fused);
        Assert.Equal(distances.OrderBy(s => s), distances);
        Assert.All(fused, s => Assert.True(s > 0, "An RRF score is a similarity and is always positive."));

        // The nearest row by distance is the LAST one a distance sort would put first if the two
        // polarities were confused: f has the smallest distance and e the largest.
        Assert.Equal("f", vector[0].Record.Key);
        Assert.Equal("e", vector[^1].Record.Key);
    }

    /// <summary>A unit vector whose dot product with <see cref="Query"/> is exactly the argument.</summary>
    private static float[] AtCosine(float cosine) => [cosine, (float)Math.Sqrt(1.0 - (cosine * cosine)), 0f, 0f];

    private static EdgeVectorStoreCollection<string, FtsVec> Collection(VectorTestHost host) =>
        new(host.Database, "docs");

    private static async Task<IReadOnlyList<VectorSearchResult<FtsVec>>> Fused(
        EdgeVectorStoreCollection<string, FtsVec> collection,
        EdgeHybridSearchOptions<FtsVec> options) =>
        await collection
            .HybridSearchAsync(
                new ReadOnlyMemory<float>(Query),
                [Keyword],
                top: 3,
                options,
                TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

    private static async Task<IReadOnlyList<string>> KeywordLaneKeysAsync(
        VectorTestHost host,
        EdgeVectorStoreCollection<string, FtsVec> collection)
    {
        var keys = await host
            .KeysByRowIdAsync(collection.Schema.DataTable, collection.Schema.RowIdColumn, collection.Schema.KeyColumn)
            .ConfigureAwait(false);
        var lane = await host
            .KeywordLaneAsync(
                collection.Schema.FullTextTable!,
                EdgeVectorSchema.BuildMatchExpression([Keyword], KeywordCombinator.Or))
            .ConfigureAwait(false);

        return [.. lane.Select(rowId => keys[rowId])];
    }

    /// <summary>
    /// The fusion table: each lane's own 1-based rank, combined with the weights and the smoothing
    /// constant exactly as the emitted SQL does, ordered by the fused score descending. No two rows
    /// of this corpus can fuse to the same score, so the order it produces is total.
    /// </summary>
    private static async Task<IReadOnlyList<(string Key, double Score)>> ExpectedFusionAsync(
        VectorTestHost host,
        EdgeVectorStoreCollection<string, FtsVec> collection,
        int rrfK,
        double vectorWeight,
        double keywordWeight)
    {
        var vectorLane = await collection
            .SearchAsync(new ReadOnlyMemory<float>(Query), top: 6, cancellationToken: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        var keywordLane = await KeywordLaneKeysAsync(host, collection).ConfigureAwait(false);

        var vectorRank = vectorLane
            .Select((hit, index) => (hit.Record.Key, Rank: index + 1))
            .ToDictionary(x => x.Key, x => x.Rank, StringComparer.Ordinal);
        var keywordRank = keywordLane
            .Select((key, index) => (Key: key, Rank: index + 1))
            .ToDictionary(x => x.Key, x => x.Rank, StringComparer.Ordinal);

        return
        [
            .. vectorRank.Keys
                .Union(keywordRank.Keys, StringComparer.Ordinal)
                .Select(key => (
                    Key: key,
                    Score: (keywordRank.TryGetValue(key, out var k) ? keywordWeight / (rrfK + k) : 0.0)
                        + (vectorRank.TryGetValue(key, out var v) ? vectorWeight / (rrfK + v) : 0.0)))
                .OrderByDescending(x => x.Score),
        ];
    }
}
