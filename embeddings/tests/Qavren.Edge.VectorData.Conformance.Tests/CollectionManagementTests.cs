using Qavren.Edge.VectorData.Conformance.Tests.Fixtures;
using VectorData.ConformanceTests;
using Xunit;

namespace Qavren.Edge.VectorData.Conformance.Tests;

/// <summary>
/// Create, exists, delete and list, against the three-table schema. <c>ListCollectionNames</c> is
/// the interesting one here: the provider must report the data table and hide its own vec0 and
/// FTS5 sidecars and their shadow tables (spec §8).
/// </summary>
/// <param name="fixture">The shared store fixture.</param>
public class EdgeCollectionManagementTests(EdgeCollectionManagementFixture fixture)
    : CollectionManagementTests<string>(fixture), IClassFixture<EdgeCollectionManagementFixture>;
