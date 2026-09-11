using Qavren.Edge.Rag.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 7 and 11's retriever half. The fake collection is written against
/// <c>Microsoft.Extensions.VectorData.Abstractions</c> alone - this project references no
/// <c>Qavren.Edge.VectorData</c> (plan adjustment 19) - so every assertion here is a property of
/// the MEVD seam rather than of sub-project 2's store.
/// </summary>
public class VectorStoreRetrieverTests
{
    private static readonly string[] Keywords = ["warranty"];

    // A deliberately WRONG projector: it sets all three of the fields the retriever owns. Every
    // test that uses it asserts the retriever overwrote them.
    private static RagSource LyingProjector(TestRecord record) =>
        new(record.Key, record.Text)
        {
            Title = record.Title,
            Score = -999d,
            ScoreKind = RetrievalScoreKind.Relevance,
            Ordinal = 42,
        };

    private static VectorStoreRetriever<string, TestRecord> Retriever(
        FakeVectorCollection collection, Action<VectorStoreRetrieverOptions<TestRecord>>? configure = null) =>
        new(collection, LyingProjector, configure);

    [Fact]
    public async Task TheHybridLaneIsTakenWhenTheCollectionCanServeItAndKeywordsArePresent()
    {
        var collection = new FakeHybridVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection);

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3, Keywords = Keywords }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal("hybrid", collection.LastLane);
        Assert.Equal(Keywords, collection.LastKeywords);
        Assert.All(sources, s => Assert.Equal(RetrievalScoreKind.Relevance, s.ScoreKind));
        Assert.Equal("chunk-c", sources[0].Id);
    }

    [Fact]
    public async Task TheVectorLaneIsTakenWhenTheCollectionHasNoKeywordLane()
    {
        var collection = new FakeVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection);

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3, Keywords = Keywords }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal("vector", collection.LastLane);
        Assert.All(sources, s => Assert.Equal(RetrievalScoreKind.Distance, s.ScoreKind));
        Assert.Equal("chunk-a", sources[0].Id);
    }

    [Fact]
    public async Task TheVectorLaneIsTakenWhenTheQuestionProducedNoKeywords()
    {
        var collection = new FakeHybridVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection);

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3 }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal("vector", collection.LastLane);
        Assert.All(sources, s => Assert.Equal(RetrievalScoreKind.Distance, s.ScoreKind));
    }

    [Fact]
    public async Task PreferHybridSearchFalseTakesTheVectorLaneOverAHybridCapableCollection()
    {
        var collection = new FakeHybridVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection, o => o.PreferHybridSearch = false);

        await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3, Keywords = Keywords }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal("vector", collection.LastLane);
    }

    [Fact]
    public async Task AProjectorThatSetsScoreScoreKindOrOrdinalHasAllThreeOverwritten()
    {
        var collection = new FakeVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection);

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3 }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.All(sources, source =>
        {
            Assert.NotEqual(-999d, source.Score);
            Assert.Equal(RetrievalScoreKind.Distance, source.ScoreKind);
            Assert.Equal(0, source.Ordinal);
        });

        // The projector's CONTENT survives; only the three scoring fields are re-stamped.
        Assert.Equal("Warranty terms", sources[0].Title);
        Assert.Equal(0.10, sources[0].Score);
    }

    [Fact]
    public async Task TheSameProjectorYieldsDistanceOnOneLaneAndRelevanceOnTheOther()
    {
        var hybrid = new FakeHybridVectorCollection("notes", TestCorpus.Rows());

        var relevance = await Retriever(hybrid)
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3, Keywords = Keywords }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var distance = await Retriever(hybrid)
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3 }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.All(relevance, s => Assert.Equal(RetrievalScoreKind.Relevance, s.ScoreKind));
        Assert.All(distance, s => Assert.Equal(RetrievalScoreKind.Distance, s.ScoreKind));
    }

    [Fact]
    public async Task ScoreThresholdInvertsItsComparisonBetweenTheDistanceAndRelevanceLanes()
    {
        // 0.15 keeps the CLOSEST row on the vector lane (distance <= 0.15 keeps chunk-a alone) and
        // the HIGHEST-scoring rows on the hybrid lane (relevance >= 0.015 keeps chunk-b and
        // chunk-c). One number, two comparisons, opposite survivors.
        var collection = new FakeHybridVectorCollection("notes", TestCorpus.Rows());

        var vector = await Retriever(collection)
            .RetrieveAsync(
                "warranty?",
                new RetrievalRequest { Top = 3, ScoreThreshold = 0.15 },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var hybrid = await Retriever(collection)
            .RetrieveAsync(
                "warranty?",
                new RetrievalRequest { Top = 3, Keywords = Keywords, ScoreThreshold = 0.015 },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(["chunk-a"], vector.Select(s => s.Id));
        Assert.Equal(["chunk-c", "chunk-b"], hybrid.Select(s => s.Id));
    }

    [Fact]
    public async Task TopPlusSkipAboveMaxCandidatesIsRefusedHereNamingTheLimit()
    {
        var retriever = Retriever(new FakeVectorCollection("notes", TestCorpus.Rows()));

        var exception = await Assert.ThrowsAsync<EdgeRagException>(async () =>
            await retriever
                .RetrieveAsync(
                    "warranty?",
                    new RetrievalRequest { Top = 4000, Skip = 97 },
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.RagRetrievalFailed, exception.Code);
        Assert.Contains("SQLITE_VEC_VEC0_K_MAX", exception.Message, StringComparison.Ordinal);
        Assert.Contains("4096", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireHybridSearchOverACollectionWithNoKeywordLaneThrows7203()
    {
        var collection = new FakeVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection, o => o.RequireHybridSearch = true);

        var exception = await Assert.ThrowsAsync<EdgeRagException>(async () =>
            await retriever
                .RetrieveAsync(
                    "warranty?",
                    new RetrievalRequest { Top = 3, Keywords = Keywords },
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.RagCollectionNotSearchable, exception.Code);
        Assert.Contains(typeof(FakeVectorCollection).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("PreferHybridSearch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireHybridSearchOverAHybridCapableCollectionWithKeywordsTakesTheHybridLane()
    {
        var collection = new FakeHybridVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection, o => o.RequireHybridSearch = true);

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3, Keywords = Keywords }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal("hybrid", collection.LastLane);
        Assert.All(sources, s => Assert.Equal(RetrievalScoreKind.Relevance, s.ScoreKind));
    }

    [Fact]
    public async Task RequireHybridSearchWithAnEmptyKeywordListStillTakesTheVectorLaneWithoutThrowing()
    {
        // The option is about the COLLECTION's capability, which is static - not about whether one
        // stop-word-only question produced any terms, which is not. The other reading turns "hi"
        // into a hard failure.
        var collection = new FakeHybridVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection, o => o.RequireHybridSearch = true);

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3, Keywords = [] }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal("vector", collection.LastLane);
        Assert.All(sources, s => Assert.Equal(RetrievalScoreKind.Distance, s.ScoreKind));
    }

    [Fact]
    public async Task TheDefaultFallsBackToTheVectorLaneSilently()
    {
        var collection = new FakeVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection);

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 3, Keywords = Keywords }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.False(retriever.Options.RequireHybridSearch);
        Assert.True(retriever.Options.PreferHybridSearch);
        Assert.Equal(4096, retriever.Options.MaxCandidates);
        Assert.Equal(3, sources.Count);
    }

    [Fact]
    public async Task TheQuestionReachesTheCollectionAsAStringSoTheStoreEmbedsItItself()
    {
        var collection = new FakeVectorCollection("notes", TestCorpus.Rows());
        var retriever = Retriever(collection);

        await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 2, Skip = 1 }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal("warranty?", collection.LastSearchValue);
        Assert.Equal(2, collection.LastTop);
        Assert.Equal(1, collection.LastSkip);
        Assert.Equal("notes", retriever.Name);
        Assert.Equal("notes", retriever.CollectionName);
    }
}
