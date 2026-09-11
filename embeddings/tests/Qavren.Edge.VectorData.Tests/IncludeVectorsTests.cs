using Microsoft.Extensions.VectorData;
using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// <c>IncludeVectors</c> on both search paths. The hybrid query needs TWO coordinated fragments for
/// this - the <c>LEFT JOIN</c> onto the vec0 table and the <c>, nv."embedding"</c> projection - and a
/// golden-SQL string comparison catches only one of them if the other is written and the expected
/// string is updated to match. A join with no matching projection costs a join, returns no vector
/// and throws nothing, because <c>VectorSearchResult</c> simply leaves the vector property at its
/// default. These assertions are what make a half-applied bracket fail.
/// </summary>
public sealed class IncludeVectorsTests
{
    private static readonly float[] QueryVector = [1f, 0f, 0f, 0f];
    private static readonly float[] AVector = [0.6f, 0.8f, 0f, 0f];
    private static readonly float[] BVector = [0f, 0.28f, -0.96f, 0f];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchAsyncRoundTripsANonEmptyVectorEqualToWhatWasUpserted()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = await SeededCollectionAsync(host);

        var hits = await collection
            .SearchAsync(
                new ReadOnlyMemory<float>(QueryVector),
                top: 2,
                new VectorSearchOptions<FtsVec> { IncludeVectors = true },
                Token)
            .ToListAsync(Token);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.False(h.Record.Embedding.IsEmpty));
        Assert.Equal(AVector, hits.Single(h => h.Record.Key == "a").Record.Embedding.ToArray());
        Assert.Equal(BVector, hits.Single(h => h.Record.Key == "b").Record.Embedding.ToArray());
    }

    [Fact]
    public async Task HybridSearchAsyncRoundTripsANonEmptyVectorEqualToWhatWasUpserted()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = await SeededCollectionAsync(host);

        var hits = await collection
            .HybridSearchAsync(
                new ReadOnlyMemory<float>(QueryVector),
                ["roof"],
                top: 2,
                new HybridSearchOptions<FtsVec> { IncludeVectors = true },
                Token)
            .ToListAsync(Token);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.False(h.Record.Embedding.IsEmpty));
        Assert.Equal(AVector, hits.Single(h => h.Record.Key == "a").Record.Embedding.ToArray());
        Assert.Equal(BVector, hits.Single(h => h.Record.Key == "b").Record.Embedding.ToArray());
    }

    [Fact]
    public async Task WithoutIncludeVectorsBothPathsLeaveTheVectorAtItsDefault()
    {
        // The other direction of the same bracket: a projection emitted unconditionally would fill
        // these in, and nothing else in the suite would notice.
        using var host = await VectorTestHost.StartAsync();
        var collection = await SeededCollectionAsync(host);

        var knn = await collection
            .SearchAsync(new ReadOnlyMemory<float>(QueryVector), top: 2, cancellationToken: Token)
            .ToListAsync(Token);
        var hybrid = await collection
            .HybridSearchAsync(new ReadOnlyMemory<float>(QueryVector), ["roof"], top: 2, cancellationToken: Token)
            .ToListAsync(Token);

        Assert.All(knn, h => Assert.True(h.Record.Embedding.IsEmpty));
        Assert.All(hybrid, h => Assert.True(h.Record.Embedding.IsEmpty));
    }

    [Fact]
    public async Task AGeneratedVectorCollectionRejectsIncludeVectorsOnBothPaths()
    {
        // A model that embeds a string source property has no vector property to read back into, so
        // the request is refused up front rather than silently returning an empty vector.
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, Note>(
            host.Database,
            "notes",
            new EdgeVectorStoreCollectionOptions { EmbeddingGenerator = new DeterministicEmbeddingGenerator(384) });
        await collection.EnsureCollectionExistsAsync(Token);
        await collection.UpsertAsync(new Note { Key = "n1", Title = "roof", Body = "roof leak" }, Token);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => collection
                .SearchAsync("roof", top: 1, new VectorSearchOptions<Note> { IncludeVectors = true }, Token)
                .ToListAsync(Token)
                .AsTask());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => collection
                .HybridSearchAsync(
                    "roof",
                    ["roof"],
                    top: 1,
                    new HybridSearchOptions<Note> { IncludeVectors = true },
                    Token)
                .ToListAsync(Token)
                .AsTask());
    }

    private static async Task<EdgeVectorStoreCollection<string, FtsVec>> SeededCollectionAsync(VectorTestHost host)
    {
        var collection = new EdgeVectorStoreCollection<string, FtsVec>(host.Database, "docs");
        await collection.EnsureCollectionExistsAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await collection.UpsertAsync(
            [
                new FtsVec { Key = "a", Body = "roof shingles", Embedding = AVector },
                new FtsVec { Key = "b", Body = "roof tiles", Embedding = BVector },
            ],
            TestContext.Current.CancellationToken).ConfigureAwait(false);

        return collection;
    }
}
