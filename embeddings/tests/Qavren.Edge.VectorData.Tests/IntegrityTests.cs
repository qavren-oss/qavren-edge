using Microsoft.Extensions.VectorData;
using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// The three tables stay in step, and the query knobs mean what the XML docs say they mean:
/// <c>Skip</c>, the two opposite <c>ScoreThreshold</c> polarities, vec0's k ceiling, the
/// empty-keyword degeneration and the FTS5 triggers on all three write paths.
/// </summary>
public sealed class IntegrityTests
{
    private const int Dimensions = 384;

    private static readonly float[] QueryVector = [1f, 0f, 0f, 0f];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After200UpsertsAnd50DeletesAllThreeTablesStillAgree()
    {
        // Half the deletes go through DeleteAsync and half through raw SQL against the same
        // IEdgeDatabase, which is what proves the cascade is a TRIGGER rather than provider code:
        // vec0 has no foreign keys, so an app deleting rows with its own SQL would otherwise orphan
        // a vector and an FTS5 document per row.
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            Enumerable.Range(0, 200).Select(i => new Note
            {
                Key = "k" + i,
                Tag = i % 2 == 0 ? "even" : "odd",
                Title = "title " + i,
                Body = "body " + i,
            }),
            Token);

        Assert.Equal(200L, await host.CountAsync("notes"));
        Assert.Equal(200L, await host.CountAsync("notes_vec"));
        Assert.Equal(200L, await host.CountAsync("notes_fts_docsize"));

        await collection.DeleteAsync(Enumerable.Range(0, 25).Select(i => "k" + i), Token);
        await host.ExecuteAsync("DELETE FROM \"notes\" WHERE \"Key\" IN ('k25','k26','k27','k28','k29')");
        // k30..k49 are rowids 31..50: the data table's _rowid is an INTEGER PRIMARY KEY assigned
        // in insert order, so this is the same twenty rows named a different way.
        await host.ExecuteAsync("DELETE FROM \"notes\" WHERE rowid BETWEEN 31 AND 50");

        Assert.Equal(150L, await host.CountAsync("notes"));
        Assert.Equal(150L, await host.CountAsync("notes_vec"));
        Assert.Equal(150L, await host.CountAsync("notes_fts_docsize"));

        // And the vec0 index agrees with the count: a KNN over everything returns 150 live rows.
        var hits = await collection.SearchAsync("body 199", top: 200, cancellationToken: Token).ToListAsync(Token);
        Assert.Equal(150, hits.Count);
        Assert.All(hits, h => Assert.DoesNotContain(h.Record.Key, DeletedKeys));
    }

    [Fact]
    public async Task SkipOnHybridSearchPagesThroughTheSameFusedOrder()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = await DocsAsync(host);

        var all = await collection
            .HybridSearchAsync(new ReadOnlyMemory<float>(QueryVector), ["roof"], top: 5, cancellationToken: Token)
            .ToListAsync(Token);
        var paged = await collection
            .HybridSearchAsync(
                new ReadOnlyMemory<float>(QueryVector),
                ["roof"],
                top: 3,
                new HybridSearchOptions<FtsVec> { Skip = 2 },
                Token)
            .ToListAsync(Token);

        Assert.Equal(3, paged.Count);
        Assert.Equal(all.Skip(2).Select(h => h.Record.Key), paged.Select(h => h.Record.Key));
    }

    [Fact]
    public async Task AVectorSearchScoreThresholdIsADistanceCeiling()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = await DocsAsync(host);

        var all = await collection
            .SearchAsync(new ReadOnlyMemory<float>(QueryVector), top: 5, cancellationToken: Token)
            .ToListAsync(Token);
        var threshold = all[1].Score!.Value;

        var limited = await collection
            .SearchAsync(
                new ReadOnlyMemory<float>(QueryVector),
                top: 5,
                new VectorSearchOptions<FtsVec> { ScoreThreshold = threshold },
                Token)
            .ToListAsync(Token);

        // Lower is better here, so the threshold keeps the NEAR rows and drops the far ones.
        Assert.Equal(2, limited.Count);
        Assert.Equal(all.Take(2).Select(h => h.Record.Key), limited.Select(h => h.Record.Key));
        Assert.All(limited, h => Assert.True(h.Score!.Value <= threshold));
    }

    [Fact]
    public async Task AHybridSearchScoreThresholdIsAScoreFloor()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = await DocsAsync(host);

        var all = await collection
            .HybridSearchAsync(new ReadOnlyMemory<float>(QueryVector), ["roof"], top: 5, cancellationToken: Token)
            .ToListAsync(Token);
        var threshold = all[1].Score!.Value;

        var limited = await collection
            .HybridSearchAsync(
                new ReadOnlyMemory<float>(QueryVector),
                ["roof"],
                top: 5,
                new HybridSearchOptions<FtsVec> { ScoreThreshold = threshold },
                Token)
            .ToListAsync(Token);

        // Higher is better here, so the same shaped option keeps the BEST rows: the exact opposite
        // of the vector path above, applied client-side as a >= after the query.
        Assert.Equal(2, limited.Count);
        Assert.Equal(all.Take(2).Select(h => h.Record.Key), limited.Select(h => h.Record.Key));
        Assert.All(limited, h => Assert.True(h.Score!.Value >= threshold));
    }

    [Fact]
    public async Task ATopAboveVec0sKMaxIsRejectedBeforeSqliteSeesIt()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = await DocsAsync(host);

        var error = await Assert.ThrowsAsync<EdgeVectorStoreException>(
            () => collection
                .SearchAsync(new ReadOnlyMemory<float>(QueryVector), top: 5000, cancellationToken: Token)
                .ToListAsync(Token)
                .AsTask());

        Assert.Equal(EdgeErrorCode.KnnLimitExceeded, error.Code);
        Assert.Contains("4096", error.Message, StringComparison.Ordinal);
        Assert.Equal(EdgeVectorStoreOperations.VectorSearch, error.OperationName);
    }

    [Fact]
    public async Task AnAllWhitespaceKeywordCollectionDegeneratesToKnnWithTheFusedScoreItWouldHaveHad()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = await DocsAsync(host);

        var knn = await collection
            .SearchAsync(new ReadOnlyMemory<float>(QueryVector), top: 4, cancellationToken: Token)
            .ToListAsync(Token);
        var degenerate = await collection
            .HybridSearchAsync(new ReadOnlyMemory<float>(QueryVector), ["", "  "], top: 4, cancellationToken: Token)
            .ToListAsync(Token);

        Assert.Equal(knn.Select(h => h.Record.Key), degenerate.Select(h => h.Record.Key));

        // The keyword half of the sum is zero everywhere, so the score is the vector half alone -
        // still an RRF score, still higher-is-better, whatever keywords the caller happened to pass.
        for (var i = 0; i < degenerate.Count; i++)
        {
            Assert.Equal(1.0 / (60 + i + 1), degenerate[i].Score!.Value, 12);
        }
    }

    [Fact]
    public async Task TheFtsTriggersFollowInsertUpdateAndDelete()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(new Note { Key = "n1", Title = "roof", Body = "shingles" }, Token);
        Assert.Equal(1, await MatchCountAsync(host, "shingles"));

        // An upsert of the same key is an UPDATE of the data row, so the AFTER UPDATE trigger is
        // what has to reindex it. A missing one leaves the old term matching forever.
        await collection.UpsertAsync(new Note { Key = "n1", Title = "roof", Body = "membrane" }, Token);
        Assert.Equal(0, await MatchCountAsync(host, "shingles"));
        Assert.Equal(1, await MatchCountAsync(host, "membrane"));

        await collection.DeleteAsync("n1", Token);
        Assert.Equal(0, await MatchCountAsync(host, "membrane"));
        Assert.Equal(0L, await host.CountAsync("notes_fts_docsize"));
        Assert.Equal(0L, await host.CountAsync("notes_vec"));
    }

    private static string[] DeletedKeys =>
        [.. Enumerable.Range(0, 50).Select(i => "k" + i)];

    private static EdgeVectorStoreCollection<string, Note> NoteCollection(VectorTestHost host) =>
        new(
            host.Database,
            "notes",
            new EdgeVectorStoreCollectionOptions
            {
                EmbeddingGenerator = new DeterministicEmbeddingGenerator(Dimensions),
            });

    /// <summary>Five pre-computed rows, so every distance and every fused score here is exact.</summary>
    private static async Task<EdgeVectorStoreCollection<string, FtsVec>> DocsAsync(VectorTestHost host)
    {
        var collection = new EdgeVectorStoreCollection<string, FtsVec>(host.Database, "docs");
        await collection.EnsureCollectionExistsAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await collection.UpsertAsync(
            [
                new FtsVec { Key = "a", Body = "roof roof shingles", Embedding = AtCosine(0.50f) },
                new FtsVec { Key = "b", Body = "roof tiles over the porch", Embedding = AtCosine(0.90f) },
                new FtsVec { Key = "c", Body = "gutter cleaning", Embedding = AtCosine(0.95f) },
                new FtsVec { Key = "d", Body = "ceiling leak", Embedding = AtCosine(0.60f) },
                new FtsVec { Key = "e", Body = "roof", Embedding = AtCosine(0.20f) },
            ],
            TestContext.Current.CancellationToken).ConfigureAwait(false);

        return collection;
    }

    private static float[] AtCosine(float cosine) => [cosine, (float)Math.Sqrt(1.0 - (cosine * cosine)), 0f, 0f];

    private static async Task<int> MatchCountAsync(VectorTestHost host, string term)
    {
        var rows = await host
            .KeywordLaneAsync("notes_fts", EdgeVectorSchema.BuildMatchExpression([term], KeywordCombinator.Or))
            .ConfigureAwait(false);
        return rows.Count;
    }
}
