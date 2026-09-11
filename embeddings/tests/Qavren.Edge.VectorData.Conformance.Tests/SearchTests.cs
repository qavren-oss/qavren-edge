using Qavren.Edge.VectorData.Conformance.Tests.Fixtures;
using VectorData.ConformanceTests;
using Xunit;

namespace Qavren.Edge.VectorData.Conformance.Tests;

/// <summary>
/// vec0 is brute-force and flat, so <see cref="Microsoft.Extensions.VectorData.IndexKind.Flat"/> is
/// the only kind this provider implements; every other kind is accepted, logged as ignored (event
/// 804) and scanned linearly. The suite only asserts <c>Flat</c>.
/// </summary>
/// <param name="fixture">The shared store fixture.</param>
public class EdgeIndexKindTests(EdgeIndexKindFixture fixture)
    : IndexKindTests<string>(fixture), IClassFixture<EdgeIndexKindFixture>;

/// <summary>
/// The three distance functions vec0 can compute. The other five MEVD names are skipped below,
/// each against the cut that causes it.
/// </summary>
/// <param name="fixture">The shared store fixture.</param>
public class EdgeDistanceFunctionTests(EdgeDistanceFunctionFixture fixture)
    : DistanceFunctionTests<string>(fixture), IClassFixture<EdgeDistanceFunctionFixture>
{
    private const string Vec0MetricCut =
        "v1 cut: vec0 exposes exactly three distance_metric values - cosine, L2 and L1 - and " +
        "EdgeCollectionModelBuilder.TryMapDistanceFunction maps only CosineDistance, " +
        "EuclideanDistance and ManhattanDistance onto them (spec §12.1). Anything else is rejected " +
        "at model build with EdgeErrorCode.UnsupportedDistanceFunction rather than silently " +
        "computed with the wrong metric.";

    private const string BitVectorCut =
        "v1 cut: HammingDistance is defined over vec0 'bit' vectors, and bit (and int8) element " +
        "types are out of scope for v1 - EdgeCollectionModelBuilder declares a single " +
        "EmbeddingGenerationDispatcher over Embedding<float> (spec §20, §12.1).";

    /// <inheritdoc />
    [Fact(Skip = Vec0MetricCut)]
    public override Task CosineSimilarity() => base.CosineSimilarity();

    /// <inheritdoc />
    [Fact(Skip = Vec0MetricCut)]
    public override Task DotProductSimilarity() => base.DotProductSimilarity();

    /// <inheritdoc />
    [Fact(Skip = Vec0MetricCut)]
    public override Task NegativeDotProductSimilarity() => base.NegativeDotProductSimilarity();

    /// <inheritdoc />
    [Fact(Skip = Vec0MetricCut)]
    public override Task EuclideanSquaredDistance() => base.EuclideanSquaredDistance();

    /// <inheritdoc />
    [Fact(Skip = BitVectorCut)]
    public override Task HammingDistance() => base.HammingDistance();
}

/// <summary>The LINQ-to-SQL filter translator, on both the typed and the dynamic path.</summary>
/// <param name="fixture">The shared collection fixture.</param>
public class EdgeFilterTests(EdgeFilterFixture fixture)
    : FilterTests<string>(fixture), IClassFixture<EdgeFilterFixture>
{
    private const string CollectionValuedPropertyCut =
        "v1 cut: collection-valued data properties (string[], List<string>) are not in " +
        "SqliteTypeMap.SupportedDataTypes (spec §12.2). Storing one needs either a JSON encoding " +
        "with its own containment operator or a child table and a join - a feature with a real API " +
        "cost and no caller yet. EdgeFilterFixture therefore omits StringArray and StringList from " +
        "the record definition, and every test that filters over them is skipped here. Scalar " +
        "Contains - an inline or captured array tested against a column, i.e. SQL IN (...) - is " +
        "supported and is NOT skipped.";

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Contains_over_field_string_array() => base.Contains_over_field_string_array();

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Contains_over_field_string_List() => base.Contains_over_field_string_List();

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Contains_with_MemoryExtensions_Contains() => base.Contains_with_MemoryExtensions_Contains();

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Contains_with_MemoryExtensions_Contains_with_null_comparer() =>
        base.Contains_with_MemoryExtensions_Contains_with_null_comparer();

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Contains_with_Enumerable_Contains() => base.Contains_with_Enumerable_Contains();

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Any_with_Contains_over_inline_string_array() =>
        base.Any_with_Contains_over_inline_string_array();

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Any_with_Contains_over_captured_string_array() =>
        base.Any_with_Contains_over_captured_string_array();

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Any_with_Contains_over_captured_string_list() =>
        base.Any_with_Contains_over_captured_string_list();

    /// <inheritdoc />
    [Fact(Skip = CollectionValuedPropertyCut)]
    public override Task Any_over_List_with_Contains_over_captured_string_array() =>
        base.Any_over_List_with_Contains_over_captured_string_array();
}

/// <summary>
/// Reciprocal rank fusion over the vec0 KNN lane and the FTS5 bm25 lane.
/// </summary>
/// <param name="vectorAndStringFixture">The single-text-property fixture.</param>
/// <param name="multiTextFixture">The two-text-property fixture.</param>
public class EdgeHybridSearchTests(
    EdgeHybridSearchFixture vectorAndStringFixture,
    EdgeHybridSearchMultiTextFixture multiTextFixture)
    : HybridSearchTests<string>(vectorAndStringFixture, multiTextFixture),
      IClassFixture<EdgeHybridSearchFixture>,
      IClassFixture<EdgeHybridSearchMultiTextFixture>;
