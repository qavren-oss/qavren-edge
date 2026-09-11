using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using VectorData.ConformanceTests.Support;

namespace Qavren.Edge.VectorData.Conformance.Tests;

/// <summary>
/// The conformance suite's <see cref="TestStore"/> over a real, temp-file
/// <see cref="IEdgeDatabase"/>. It is deliberately NOT a mock: the database is opened through
/// sub-project 1's <c>UseSqliteNative().AddSqlite(...)</c> pipeline, so every conformance
/// assertion runs against the pinned SQLite build with <c>vec0</c> and FTS5 compiled in - the
/// same natives the shipping app loads.
/// <para>
/// One instance is shared by every fixture in the assembly (<see cref="Instance"/>). The base
/// class reference-counts <see cref="StartAsync"/>/<see cref="StopAsync"/>, so the host starts
/// once for the whole run and the suites share one database file, which is exactly how a real
/// consumer uses sub-project 1: one <see cref="IEdgeDatabase"/>, many collections.
/// </para>
/// </summary>
public sealed class EdgeTestStore : TestStore
{
    /// <summary>The file name every host in this assembly opens.</summary>
    public const string DatabaseFileName = "conformance.db";

    private ServiceProvider? _services;
    private string? _root;

    private EdgeTestStore()
    {
    }

    /// <summary>The single instance every fixture in this assembly returns from <c>TestStore</c>.</summary>
    public static EdgeTestStore Instance { get; } = new();

    /// <summary>The scratch directory holding the database file. Valid only between start and stop.</summary>
    public string Root => _root ?? throw new InvalidOperationException("The test store is not started.");

    /// <summary>The started database. Valid only between start and stop.</summary>
    public IEdgeDatabase Database =>
        (_services ?? throw new InvalidOperationException("The test store is not started."))
        .GetRequiredService<IEdgeDatabase>();

    /// <summary>
    /// vec0 stores float32 exactly, so a vector read back compares equal to the one upserted.
    /// </summary>
    public override bool VectorsComparable => true;

    /// <summary>
    /// <c>VectorSearchOptions.ScoreThreshold</c> is pushed into the KNN SQL as
    /// <c>v.distance &lt;= $t</c>, so the suite exercises the real predicate rather than asserting
    /// a <see cref="NotSupportedException"/>.
    /// </summary>
    public override bool SupportsScoreThreshold => true;

    /// <summary>
    /// <b>Not</b> the base class's <c>CosineSimilarity</c>. vec0 computes distances, not
    /// similarities, and this provider maps exactly three MEVD distance functions onto vec0's
    /// three metrics (cosine, L2, L1). Leaving the base default in place would make every fixture
    /// that does not name a distance function fail at model build with
    /// <c>UnsupportedDistanceFunction</c>.
    /// </summary>
    public override string DefaultDistanceFunction => DistanceFunction.CosineDistance;

    /// <summary>vec0 is brute-force; <see cref="IndexKind.Flat"/> is the only honest answer.</summary>
    public override string DefaultIndexKind => IndexKind.Flat;

    /// <summary>Builds a second store over the same database, with a different generator wired in.</summary>
    /// <param name="embeddingGenerator">The document generator, or null for none.</param>
    /// <param name="queryEmbeddingGenerator">The query generator, or null to reuse the document one.</param>
    /// <returns>A store sharing this one's database.</returns>
    public EdgeVectorStore CreateVectorStore(
        IEmbeddingGenerator? embeddingGenerator = null,
        IEmbeddingGenerator? queryEmbeddingGenerator = null) =>
        new(Database, new EdgeVectorStoreOptions(), embeddingGenerator, queryEmbeddingGenerator);

    /// <inheritdoc />
    protected override async Task StartAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "qedge-conformance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var services = new ServiceCollection();
        services.AddLogging();

        // AddQavrenEdge registers IEdgePaths with TryAddSingleton, so this has to come first.
        services.AddSingleton<IEdgePaths>(new FixedPaths(_root));
        services.AddQavrenEdge(edge =>
        {
            edge.UseSqliteNative();
            edge.AddSqlite(o =>
            {
                o.DatabaseName = DatabaseFileName;
                o.Directory = _root;
            });
        });

        _services = services.BuildServiceProvider();
        await _services.GetRequiredService<IEdgeHost>().EnsureStartedAsync().ConfigureAwait(false);

        DefaultVectorStore = new EdgeVectorStore(_services.GetRequiredService<IEdgeDatabase>());
    }

    /// <inheritdoc />
    protected override async Task StopAsync()
    {
        if (_services is not null)
        {
            SqliteConnection.ClearAllPools();
            await _services.DisposeAsync().ConfigureAwait(false);
            _services = null;
        }

        if (_root is not null)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A WAL file may still be mapped on Windows; a leftover temp directory is harmless.
            }

            _root = null;
        }
    }

    private sealed class FixedPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
