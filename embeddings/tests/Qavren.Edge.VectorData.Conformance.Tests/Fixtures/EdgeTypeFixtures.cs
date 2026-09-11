using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using VectorData.ConformanceTests;
using VectorData.ConformanceTests.Support;
using VectorData.ConformanceTests.TypeTests;

namespace Qavren.Edge.VectorData.Conformance.Tests.Fixtures;

/// <summary>
/// Every default data type the suite knows about, minus the three this provider does not store.
/// Declaring them here is not a skip: the suite reads <see cref="UnsupportedDefaultTypes"/> when it
/// builds the record definition, so the column is never created and the corresponding test is a
/// no-op rather than a false pass.
/// </summary>
public sealed class EdgeDataTypeFixture : DataTypeTests<string, DataTypeTests<string>.DefaultRecord>.Fixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;

    /// <summary>
    /// <c>byte</c>, <c>decimal</c> and <c>string[]</c> are not in
    /// <c>SqliteTypeMap.SupportedDataTypes</c> (spec §12.2). <c>byte</c> and <c>decimal</c> have no
    /// lossless SQLite affinity that Microsoft.Data.Sqlite reads back without a converter, and a
    /// <c>string[]</c> column would need either a JSON encoding or a child table - both are
    /// features with an API cost and no caller yet.
    /// </summary>
    public override Type[] UnsupportedDefaultTypes { get; } = [typeof(byte), typeof(decimal), typeof(string[])];
}

/// <summary>
/// The <see cref="EmbeddingGenerationTests{TKey}"/> string-source fixture: a <c>string</c> vector
/// property that an <see cref="IEmbeddingGenerator"/> turns into a float32 embedding on write.
/// </summary>
public sealed class EdgeStringVectorFixture : EmbeddingGenerationTests<string>.StringVectorFixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;

    /// <inheritdoc />
    public override VectorStore CreateVectorStore(IEmbeddingGenerator? embeddingGenerator = null) =>
        EdgeTestStore.Instance.CreateVectorStore(embeddingGenerator);

    /// <inheritdoc />
    public override Func<IServiceCollection, IServiceCollection>[] DependencyInjectionStoreRegistrationDelegates { get; } =
        [services => EdgeTestStore.Instance.RegisterStore(services)];

    /// <summary>
    /// Empty, and the matching test is skipped with the cut named. This provider registers the
    /// <c>VectorStore</c> in DI (<c>AddVectorStore</c>) and nothing else: a collection is obtained
    /// from the store, and <c>AddVectorCollectionMigration</c> registers schema, not a resolvable
    /// <c>VectorStoreCollection&lt;TKey, TRecord&gt;</c> service (spec §8, §14.1).
    /// </summary>
    public override Func<IServiceCollection, IServiceCollection>[] DependencyInjectionCollectionRegistrationDelegates { get; } = [];
}

/// <summary>
/// The <see cref="EmbeddingGenerationTests{TKey}"/> search-only fixture: a stored
/// <c>ReadOnlyMemory&lt;float&gt;</c> vector with a generator used only to embed the query.
/// </summary>
public sealed class EdgeRomOfFloatVectorFixture : EmbeddingGenerationTests<string>.RomOfFloatVectorFixture
{
    /// <inheritdoc />
    public override TestStore TestStore => EdgeTestStore.Instance;

    /// <inheritdoc />
    public override VectorStore CreateVectorStore(IEmbeddingGenerator? embeddingGenerator = null) =>
        EdgeTestStore.Instance.CreateVectorStore(embeddingGenerator);

    /// <inheritdoc />
    public override Func<IServiceCollection, IServiceCollection>[] DependencyInjectionStoreRegistrationDelegates { get; } =
        [services => EdgeTestStore.Instance.RegisterStore(services)];

    /// <inheritdoc cref="EdgeStringVectorFixture.DependencyInjectionCollectionRegistrationDelegates" />
    public override Func<IServiceCollection, IServiceCollection>[] DependencyInjectionCollectionRegistrationDelegates { get; } = [];
}

/// <summary>
/// Registers a second Qavren.Edge host over the <b>same</b> database file the shared test store
/// opened, through the package's real DI entry point - <c>AddQavrenEdge(edge =&gt;
/// edge.AddVectorStore())</c> - so the suite's DI assertions exercise the generator-resolution
/// factory rather than a hand-built store.
/// </summary>
internal static class EdgeTestStoreDependencyInjection
{
    internal static IServiceCollection RegisterStore(this EdgeTestStore store, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new RootedPaths(store.Root));
        services.AddQavrenEdge(edge =>
        {
            edge.UseSqliteNative();
            edge.AddSqlite(o =>
            {
                o.DatabaseName = EdgeTestStore.DatabaseFileName;
                o.Directory = store.Root;
            });
            edge.AddVectorStore();
        });

        return services;
    }

    private sealed class RootedPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
