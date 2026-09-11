using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;

namespace Qavren.Edge.VectorData.Conformance.Tests;

/// <summary>
/// The meta-test: every abstract suite the package exports must have a concrete implementation in
/// this assembly, or appear in <see cref="IgnoredTestBases"/> with the cut that removes it named
/// here <b>and</b> in <c>SkipList.md</c>. A suite that is quietly dropped is a coverage hole the
/// gate would otherwise not see.
/// </summary>
public class EdgeTestSuiteImplementationTests : TestSuiteImplementationTests
{
    /// <summary>
    /// Three suites, three v1 cuts.
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>MultiVectorModelTests</c> - multiple vector properties per collection are out of scope
    /// (spec §20). <c>EdgeCollectionModelBuilder</c> sets
    /// <c>CollectionModelBuildingOptions.SupportsMultipleVectors = false</c> and raises
    /// <c>MultipleVectorPropertiesUnsupported</c>: a second vector property means a second vec0
    /// table and a materially worse hybrid query.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>NoVectorModelTests</c> - a collection with no vector property is out of scope. The
    /// provider's schema is one data table plus a vec0 sidecar plus an optional FTS5 sidecar
    /// (spec §12.1), so <c>RequiresAtLeastOneVector</c> is true; a vectorless collection is a plain
    /// SQLite table, which sub-project 1 already offers without this package.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>DependencyInjectionTests</c> (both arities) - the suite is written against MEVD's
    /// <c>IServiceCollection.AddXxxVectorStore(serviceKey, lifetime)</c> registration shape. This
    /// package registers through sub-project 1's <c>EdgeBuilder</c> instead -
    /// <c>AddQavrenEdge(edge =&gt; edge.AddVectorStore())</c> - because a store here is bound to an
    /// <c>IEdgeDatabase</c> rather than to a connection string (spec §8, §14.1), and there is no
    /// per-collection service registration at all. The delegate signatures the suite requires do
    /// not exist on this provider, so the suite is not applicable rather than failing.
    /// </description>
    /// </item>
    /// </list>
    /// </summary>
    protected override ICollection<Type> IgnoredTestBases { get; } =
    [
        typeof(MultiVectorModelTests<>),
        typeof(NoVectorModelTests<>),
        typeof(DependencyInjectionTests<>),
        typeof(DependencyInjectionTests<,,,>),
    ];
}
