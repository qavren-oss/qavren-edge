using Microsoft.Extensions.VectorData;
using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;
using VectorData.ConformanceTests.Support;
using VectorData.ConformanceTests.TypeTests;

namespace Qavren.Edge.VectorData.Conformance.Tests.Fixtures;

/// <summary>
/// The plain store fixture, for suites that create their own collections.
/// </summary>
public class EdgeVectorStoreFixture : VectorStoreFixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>The <see cref="CollectionManagementTests{TKey}"/> fixture.</summary>
public sealed class EdgeCollectionManagementFixture : EdgeVectorStoreFixture;

/// <summary>The <see cref="BasicModelTests{TKey}"/> fixture.</summary>
public sealed class EdgeBasicModelFixture : BasicModelTests<string>.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>The <see cref="DynamicModelTests{TKey}"/> fixture - the dynamic record path.</summary>
public sealed class EdgeDynamicModelFixture : DynamicModelTests<string>.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>The <see cref="NoDataModelTests{TKey}"/> fixture.</summary>
public sealed class EdgeNoDataModelFixture : NoDataModelTests<string>.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>The <see cref="IndexKindTests{TKey}"/> fixture.</summary>
public sealed class EdgeIndexKindFixture : IndexKindTests<string>.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>The <see cref="DistanceFunctionTests{TKey}"/> fixture.</summary>
public sealed class EdgeDistanceFunctionFixture : DistanceFunctionTests<string>.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>
/// The <see cref="FilterTests{TKey}"/> fixture, with two provider-shaped adjustments to the record
/// definition. Both are the mechanism the suite itself provides - <c>AssertEqualFilterRecord</c>
/// and <c>AssertEqualDynamic</c> both re-read <c>CreateRecordDefinition()</c> and assert only over
/// the properties it declares.
/// <list type="number">
/// <item>
/// <description>
/// The vector property is declared <c>ReadOnlyMemory&lt;float&gt;</c>, not
/// <c>ReadOnlyMemory&lt;float&gt;?</c>. vec0 overloads SQL NULL on a vector column to mean "no
/// change", so a nullable vector property makes writing a null vector a silent no-op; the provider
/// rejects one at model build with <c>NullableVectorProperty</c> (spec §12.1). The suite's own
/// fixture never writes a null vector, so the non-nullable declaration changes nothing it asserts.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>StringArray</c> and <c>StringList</c> are dropped. Collection-valued data properties are not
/// in <c>SqliteTypeMap.SupportedDataTypes</c>; the tests that filter over them are skipped in
/// <c>EdgeFilterTests</c> with that cut named.
/// </description>
/// </item>
/// </list>
/// </summary>
public sealed class EdgeFilterFixture : FilterTests<string>.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;

    /// <inheritdoc />
    public override VectorStoreCollectionDefinition CreateRecordDefinition() =>
        new()
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(FilterTests<string>.FilterRecord.Key), typeof(string)),
                new VectorStoreVectorProperty(
                    nameof(FilterTests<string>.FilterRecord.Vector),
                    typeof(ReadOnlyMemory<float>),
                    dimensions: 3)
                {
                    DistanceFunction = TestStore.DefaultDistanceFunction,
                    IndexKind = TestStore.DefaultIndexKind,
                },
                new VectorStoreDataProperty(nameof(FilterTests<string>.FilterRecord.Int), typeof(int)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(FilterTests<string>.FilterRecord.String), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(FilterTests<string>.FilterRecord.Bool), typeof(bool)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(FilterTests<string>.FilterRecord.Int2), typeof(int)) { IsIndexed = true },
            ],
        };
}

/// <summary>The <see cref="KeyTypeTests"/> fixture.</summary>
public sealed class EdgeKeyTypeFixture : KeyTypeTests.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>The <see cref="EmbeddingTypeTests{TKey}"/> fixture.</summary>
public sealed class EdgeEmbeddingTypeFixture : EmbeddingTypeTests<string>.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>The vector-plus-string hybrid fixture.</summary>
public sealed class EdgeHybridSearchFixture : HybridSearchTests<string>.VectorAndStringFixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}

/// <summary>The two-text-property hybrid fixture.</summary>
public sealed class EdgeHybridSearchMultiTextFixture : HybridSearchTests<string>.MultiTextFixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;
}
