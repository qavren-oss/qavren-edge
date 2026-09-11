using Qavren.Edge.VectorData.Conformance.Tests.Fixtures;
using VectorData.ConformanceTests.TypeTests;
using Xunit;

namespace Qavren.Edge.VectorData.Conformance.Tests;

/// <summary>
/// Every supported data-property CLR type, round-tripped and filtered on, through both the
/// reflecting and the dynamic record path (the suite exercises both inside each test).
/// </summary>
/// <param name="fixture">The shared collection fixture.</param>
public class EdgeDataTypeTests(EdgeDataTypeFixture fixture)
    : DataTypeTests<string, DataTypeTests<string>.DefaultRecord>(fixture), IClassFixture<EdgeDataTypeFixture>;

/// <summary>
/// The three float32 vector shapes: <c>ReadOnlyMemory&lt;float&gt;</c>,
/// <c>Embedding&lt;float&gt;</c> and <c>float[]</c>.
/// </summary>
/// <param name="fixture">The shared store fixture.</param>
public class EdgeEmbeddingTypeTests(EdgeEmbeddingTypeFixture fixture)
    : EmbeddingTypeTests<string>(fixture), IClassFixture<EdgeEmbeddingTypeFixture>;

/// <summary>Guid keys, and the key-type mismatch diagnostic.</summary>
/// <param name="fixture">The shared store fixture.</param>
public class EdgeKeyTypeTests(EdgeKeyTypeFixture fixture)
    : KeyTypeTests(fixture), IClassFixture<EdgeKeyTypeFixture>;
