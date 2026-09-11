using Qavren.Edge.VectorData.Conformance.Tests.Fixtures;
using VectorData.ConformanceTests;
using Xunit;

namespace Qavren.Edge.VectorData.Conformance.Tests;

/// <summary>
/// Embedding generation at the store, collection and property level, plus the search-only
/// (query-side) generator - the seam <c>EdgeVectorData.QueryGeneratorServiceKey</c> exists for.
/// </summary>
/// <param name="stringVectorFixture">The string-source-property fixture.</param>
/// <param name="romOfFloatVectorFixture">The stored-vector, search-only-generator fixture.</param>
public class EdgeEmbeddingGenerationTests(
    EdgeStringVectorFixture stringVectorFixture,
    EdgeRomOfFloatVectorFixture romOfFloatVectorFixture)
    : EmbeddingGenerationTests<string>(stringVectorFixture, romOfFloatVectorFixture),
      IClassFixture<EdgeStringVectorFixture>,
      IClassFixture<EdgeRomOfFloatVectorFixture>
{
    private const string NoCollectionRegistrationCut =
        "v1 cut: this provider registers the VectorStore in DI and nothing else. AddVectorStore " +
        "registers EdgeVectorStore and MEVD's VectorStore; a collection is obtained from the store, " +
        "and AddVectorCollectionMigration registers schema rather than a resolvable " +
        "VectorStoreCollection<TKey, TRecord> service (spec §8, §14.1). " +
        "DependencyInjectionCollectionRegistrationDelegates is therefore empty, and this test is " +
        "skipped rather than left to iterate an empty array and pass vacuously.";

    /// <inheritdoc />
    [Fact(Skip = NoCollectionRegistrationCut)]
    public override Task SearchAsync_with_collection_dependency_injection() =>
        base.SearchAsync_with_collection_dependency_injection();
}
