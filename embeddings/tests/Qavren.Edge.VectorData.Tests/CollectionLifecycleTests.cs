using Microsoft.Extensions.VectorData;
using Qavren.Edge.Sqlite.Vec;
using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// Tier 2: real connections, the real win-x64 native, real <c>vec0</c> and FTS5 tables. Nothing
/// here is mocked.
/// </summary>
public sealed class CollectionLifecycleTests
{
    private const int Dimensions = 384;

    private static readonly float[] UnitX = [1f, 0f, 0f, 0f];
    private static readonly float[] UnitY = [0f, 1f, 0f, 0f];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AStoredVectorIsTheRawLittleEndianFloat32BlobSp1sVecBlobProduces()
    {
        // A record with a PRE-COMPUTED ReadOnlyMemory<float> vector, not Note's string source: no
        // generator in the path, so the bytes on disk are the bytes RecordMapper wrote and nothing
        // else. This asserts the BYTES IN THE DATABASE, not a round trip - a hand-rolled encoder
        // would pass a round trip against its own decoder.
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, RawVec>(host.Database, "raw");
        await collection.EnsureCollectionExistsAsync(Token);

        var vector = new[] { 1f, -2.5f, 0f, float.MaxValue };
        await collection.UpsertAsync(new RawVec { Key = "k", Embedding = vector }, Token);

        var blob = await host.ReadBlobAsync("raw_vec", "embedding", rowId: 1);

        Assert.Equal(VecBlob.From(vector), blob);
        Assert.Equal(vector.Length * sizeof(float), blob.Length);
        Assert.Equal(vector, VecBlob.ToFloats(blob));
    }

    [Fact]
    public async Task APreComputedVectorStoreNeedsNoGeneratorAtAll()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, RawVec>(host.Database, "raw");
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(new RawVec { Key = "a", Embedding = UnitX }, Token);
        await collection.UpsertAsync(new RawVec { Key = "b", Embedding = UnitY }, Token);

        var hits = await collection
            .SearchAsync(new ReadOnlyMemory<float>(UnitX), top: 2, cancellationToken: Token)
            .ToListAsync(Token);

        Assert.Equal("a", hits[0].Record.Key);
        Assert.Equal("b", hits[1].Record.Key);
        Assert.True(hits[0].Score < hits[1].Score, "vec0 returns a DISTANCE, so lower is better.");
    }

    [Fact]
    public async Task EnsureCollectionExistsCreatesTheDataTableBothSidecarsAndEveryTrigger()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);

        Assert.False(await collection.CollectionExistsAsync(Token));
        await collection.EnsureCollectionExistsAsync(Token);
        Assert.True(await collection.CollectionExistsAsync(Token));

        Assert.Equal(0L, await host.CountAsync("notes"));
        Assert.Equal(0L, await host.CountAsync("notes_vec"));
        Assert.Equal(0L, await host.CountAsync("notes_fts_docsize"));

        var triggers = await host.AllTriggerNamesAsync();
        Assert.Contains("notes_vec_ad", triggers);
        Assert.Contains("notes_fts_ai", triggers);
        Assert.Contains("notes_fts_au", triggers);
        Assert.Contains("notes_fts_ad", triggers);
    }

    [Fact]
    public async Task EnsureCollectionExistsIsIdempotent()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);

        await collection.EnsureCollectionExistsAsync(Token);
        await collection.EnsureCollectionExistsAsync(Token);

        Assert.True(await collection.CollectionExistsAsync(Token));
    }

    [Fact]
    public async Task EnsureCollectionDeletedRemovesAllThreeTablesAndEveryTrigger()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.EnsureCollectionDeletedAsync(Token);

        Assert.False(await collection.CollectionExistsAsync(Token));
        var tables = await host.AllTableNamesAsync();
        Assert.DoesNotContain("notes", tables);
        Assert.DoesNotContain("notes_vec", tables);
        Assert.DoesNotContain("notes_fts", tables);
        Assert.Empty(await host.AllTriggerNamesAsync());
    }

    [Fact]
    public async Task AStringSourcePropertyIsEmbeddedOnUpsertAndRoundTrips()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            new Note { Key = "n1", Tag = "t", Title = "Title", Body = "the body" },
            Token);

        var read = await collection.GetAsync("n1", cancellationToken: Token);
        Assert.NotNull(read);
        Assert.Equal("Title", read.Title);
        Assert.Equal("the body", read.Body);
        Assert.Equal("t", read.Tag);

        // One vec0 row and one FTS5 document, both keyed on the data table's rowid.
        Assert.Equal(1L, await host.CountAsync("notes"));
        Assert.Equal(1L, await host.CountAsync("notes_vec"));
        Assert.Equal(1L, await host.CountAsync("notes_fts_docsize"));
    }

    [Fact]
    public async Task AnUpsertOfTheSameKeyReplacesTheRowRatherThanAddingOne()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(new Note { Key = "n1", Title = "first", Body = "first body" }, Token);
        await collection.UpsertAsync(new Note { Key = "n1", Title = "second", Body = "second body" }, Token);

        Assert.Equal(1L, await host.CountAsync("notes"));
        Assert.Equal(1L, await host.CountAsync("notes_vec"));
        Assert.Equal("second", (await collection.GetAsync("n1", cancellationToken: Token))!.Title);
    }

    [Fact]
    public async Task ABatchUpsertWritesEveryRecordInOneTransaction()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            Enumerable.Range(0, 25).Select(i => new Note
            {
                Key = "k" + i,
                Title = "title " + i,
                Body = "body " + i,
            }),
            Token);

        Assert.Equal(25L, await host.CountAsync("notes"));
        Assert.Equal(25L, await host.CountAsync("notes_vec"));
        Assert.Equal(25L, await host.CountAsync("notes_fts_docsize"));
    }

    [Fact]
    public async Task DeletingARowWithTheAppsOwnSqlStillCleansVec0AndFts5()
    {
        // The cascade is a trigger, not provider code, which is the whole reason this goes through
        // raw SQL against the same IEdgeDatabase rather than through DeleteAsync.
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(new Note { Key = "n1", Title = "t", Body = "b" }, Token);
        await host.ExecuteAsync("DELETE FROM \"notes\" WHERE \"Key\" = 'n1'");

        Assert.Equal(0L, await host.CountAsync("notes"));
        Assert.Equal(0L, await host.CountAsync("notes_vec"));
        Assert.Equal(0L, await host.CountAsync("notes_fts_docsize"));
    }

    [Fact]
    public async Task DeleteAsyncRemovesTheRowAndItsSidecars()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(new Note { Key = "n1", Title = "t", Body = "b" }, Token);
        await collection.DeleteAsync("n1", Token);

        Assert.Null(await collection.GetAsync("n1", cancellationToken: Token));
        Assert.Equal(0L, await host.CountAsync("notes_vec"));
        Assert.Equal(0L, await host.CountAsync("notes_fts_docsize"));
    }

    [Fact]
    public async Task HybridScoreIsASimilarityWhereHigherIsBetter_TheOppositeOfSearchAsyncsDistance()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            [
                new Note { Key = "a", Title = "alpha", Body = "the quick brown fox" },
                new Note { Key = "b", Title = "beta", Body = "a lazy dog sleeps" },
                new Note { Key = "c", Title = "gamma", Body = "nothing in common" },
            ],
            Token);

        var hits = await collection
            .HybridSearchAsync(
                "the quick brown fox",
                ["quick", "fox"],
                top: 3,
                // Note has TWO full-text columns, and MEVD's GetFullTextDataPropertyOrSingle - the
                // resolution the spec names - refuses to guess between them. Naming the lane is the
                // contract, not a workaround.
                new HybridSearchOptions<Note> { AdditionalProperty = n => n.Body },
                Token)
            .ToListAsync(Token);

        Assert.NotEmpty(hits);
        Assert.Equal("a", hits[0].Record.Key);

        var scores = hits.Select(h => h.Score!.Value).ToArray();
        Assert.Equal(scores.OrderByDescending(s => s), scores);
    }

    [Fact]
    public async Task AnEmptyKeywordCollectionDegeneratesToPlainKnnRatherThanAMalformedMatch()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            [
                new Note { Key = "a", Title = "alpha", Body = "the quick brown fox" },
                new Note { Key = "b", Title = "beta", Body = "a lazy dog sleeps" },
            ],
            Token);

        var hits = await collection
            .HybridSearchAsync("the quick brown fox", ["   ", ""], top: 2, cancellationToken: Token)
            .ToListAsync(Token);

        Assert.Equal(2, hits.Count);
        Assert.Equal("a", hits[0].Record.Key);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task AKeywordCarryingFts5OperatorsIsAPhraseAndNeverAnOperator()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(new Note { Key = "a", Title = "alpha", Body = "harmless text" }, Token);

        // Any of these reaching FTS5 unquoted is a syntax error, not a result set.
        var hits = await collection
            .HybridSearchAsync(
                "harmless text",
                ["harmless\" OR x:(", "NEAR("],
                top: 2,
                new HybridSearchOptions<Note> { AdditionalProperty = n => n.Body },
                Token)
            .ToListAsync(Token);

        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task HybridSearchWithoutAFullTextPropertyNamesTheAttributeToAdd()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, RawVec>(host.Database, "raw");
        await collection.EnsureCollectionExistsAsync(Token);

        var error = await Assert.ThrowsAsync<EdgeVectorModelException>(
            () => collection
                .HybridSearchAsync(new ReadOnlyMemory<float>(UnitX), ["x"], top: 1, cancellationToken: Token)
                .ToListAsync(Token)
                .AsTask());

        Assert.Equal(EdgeErrorCode.FullTextPropertyMissing, error.Code);
        Assert.Contains("IsFullTextIndexed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGeneratorWhoseWidthDisagreesWithTheDeclaredVectorIsRejectedAtCollectionCreate()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, Note>(
            host.Database,
            "notes",
            new EdgeVectorStoreCollectionOptions { EmbeddingGenerator = new DeterministicEmbeddingGenerator(7) });

        var error = await Assert.ThrowsAsync<EdgeVectorModelException>(
            () => collection.EnsureCollectionExistsAsync(Token));

        Assert.Equal(EdgeErrorCode.VectorDimensionMismatch, error.Code);
        Assert.Contains("384", error.Message, StringComparison.Ordinal);
        Assert.Contains("7-d", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopPlusSkipAboveTheVec0LimitIsRejectedBeforeSqliteSeesIt()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        var error = await Assert.ThrowsAsync<EdgeVectorStoreException>(
            () => collection
                .SearchAsync("anything", top: 4000, new VectorSearchOptions<Note> { Skip = 200 }, Token)
                .ToListAsync(Token)
                .AsTask());

        Assert.Equal(EdgeErrorCode.KnnLimitExceeded, error.Code);
        Assert.Contains("4096", error.Message, StringComparison.Ordinal);
        Assert.Equal("VectorSearch", error.OperationName);
    }

    [Fact]
    public async Task SkipIsHonouredClientSideOverKEqualsTopPlusSkip()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            Enumerable.Range(0, 10).Select(i => new Note
            {
                Key = "k" + i,
                Title = "t" + i,
                Body = "body " + i,
            }),
            Token);

        var all = await collection.SearchAsync("body 3", top: 10, cancellationToken: Token).ToListAsync(Token);
        var skipped = await collection
            .SearchAsync("body 3", top: 8, new VectorSearchOptions<Note> { Skip = 2 }, Token)
            .ToListAsync(Token);

        Assert.Equal(8, skipped.Count);
        Assert.Equal(all.Skip(2).Select(h => h.Record.Key), skipped.Select(h => h.Record.Key));
    }

    [Fact]
    public async Task AFilterIsAPreFilterNotAPostFilter()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            Enumerable.Range(0, 40).Select(i => new Note
            {
                Key = "k" + i,
                Tag = i % 10 == 0 ? "keep" : "drop",
                Title = "t" + i,
                Body = "body " + i,
            }),
            Token);

        var hits = await collection
            .SearchAsync("body 7", top: 4, new VectorSearchOptions<Note> { Filter = n => n.Tag == "keep" }, Token)
            .ToListAsync(Token);

        // Four MATCHING rows come back, not "the four nearest overall, then filtered down".
        Assert.Equal(4, hits.Count);
        Assert.All(hits, h => Assert.Equal("keep", h.Record.Tag));
    }

    [Fact]
    public async Task GetByFilterReadsThroughTheDataTable()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = NoteCollection(host);
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            [
                new Note { Key = "a", Tag = "x", Title = "one", Body = "b1" },
                new Note { Key = "b", Tag = "y", Title = "two", Body = "b2" },
            ],
            Token);

        var found = await collection
            .GetAsync(n => n.Tag == "y", top: 10, cancellationToken: Token)
            .ToListAsync(Token);

        Assert.Single(found);
        Assert.Equal("b", found[0].Key);
    }

    [Fact]
    public async Task IncludeVectorsRoundTripsThePreComputedVector()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, RawVec>(host.Database, "raw");
        await collection.EnsureCollectionExistsAsync(Token);

        var vector = new[] { 0.25f, 0.5f, -0.75f, 1f };
        await collection.UpsertAsync(new RawVec { Key = "k", Embedding = vector }, Token);

        var read = await collection.GetAsync("k", new RecordRetrievalOptions { IncludeVectors = true }, Token);

        Assert.NotNull(read);
        Assert.Equal(vector, read.Embedding.ToArray());
    }

    [Fact]
    public async Task AnOperationOnACollectionThatWasNeverCreatedIsVectorCollectionNotFound()
    {
        // errors.md 5201: "the requested collection name has no matching table". SQLite reports
        // that as SQLITE_ERROR "no such table", and Wrap gives it its own code rather than the
        // catch-all VectorStoreOperationFailed - otherwise a typo in a collection name is
        // indistinguishable from a disk error.
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, RawVec>(host.Database, "never_created");

        var ex = await Assert.ThrowsAsync<EdgeVectorStoreException>(
            () => collection.UpsertAsync(new RawVec { Key = "k", Embedding = UnitX }, Token));

        Assert.Equal(EdgeErrorCode.VectorCollectionNotFound, ex.Code);
        Assert.Equal("never_created", ex.CollectionName);
        Assert.Equal(EdgeVectorStoreOperations.Upsert, ex.OperationName);
    }

    private static EdgeVectorStoreCollection<string, Note> NoteCollection(VectorTestHost host) =>
        new(
            host.Database,
            "notes",
            new EdgeVectorStoreCollectionOptions
            {
                EmbeddingGenerator = new DeterministicEmbeddingGenerator(Dimensions),
            });
}
