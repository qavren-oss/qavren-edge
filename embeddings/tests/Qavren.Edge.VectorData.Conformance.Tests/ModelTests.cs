using Qavren.Edge.VectorData.Conformance.Tests.Fixtures;
using VectorData.ConformanceTests.ModelTests;
using Xunit;

namespace Qavren.Edge.VectorData.Conformance.Tests;

/// <summary>
/// The reflecting record path: CRUD, filtered <c>GetAsync</c>, ordering, paging and KNN over
/// <c>EdgeVectorStoreCollection&lt;TKey, TRecord&gt;</c>.
/// </summary>
/// <param name="fixture">The shared collection fixture.</param>
public class EdgeBasicModelTests(EdgeBasicModelFixture fixture)
    : BasicModelTests<string>(fixture), IClassFixture<EdgeBasicModelFixture>;

/// <summary>
/// The dynamic record path over the same suite of assertions. Spec §8 makes
/// <c>EdgeDynamicVectorStoreCollection</c> first-class rather than a trimmed-down subset, and this
/// class is where that claim is tested.
/// </summary>
/// <param name="fixture">The shared dynamic collection fixture.</param>
public class EdgeDynamicModelTests(EdgeDynamicModelFixture fixture)
    : DynamicModelTests<string>(fixture), IClassFixture<EdgeDynamicModelFixture>;

/// <summary>A record with a key and a vector and no data properties.</summary>
/// <param name="fixture">The shared collection fixture.</param>
public class EdgeNoDataModelTests(EdgeNoDataModelFixture fixture)
    : NoDataModelTests<string>(fixture), IClassFixture<EdgeNoDataModelFixture>;
