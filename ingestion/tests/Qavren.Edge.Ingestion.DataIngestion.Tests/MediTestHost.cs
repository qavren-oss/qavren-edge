using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.VectorData;
using Xunit;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Ingestion.DataIngestion.Tests;

/// <summary>
/// A fully wired Qavren.Edge host over a scratch directory: SP1's SQLite with the real native
/// (vec0 + FTS5), SP2's store, and SP3's <c>AddIngestion(1)</c> so the collection the MEDI writer
/// targets is the very one <c>IIngestionPipeline</c> would write — same DDL, same migration.
/// </summary>
internal sealed class MediTestHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _root;

    private MediTestHost(ServiceProvider services, string root, RecordingEmbeddingGenerator generator)
    {
        _services = services;
        _root = root;
        Generator = generator;
    }

    public IServiceProvider Services => _services;

    public string Root => _root;

    public RecordingEmbeddingGenerator Generator { get; }

    public WhitespaceTokenizer Tokenizer { get; } = new();

    public IEdgeDatabase Database => _services.GetRequiredService<IEdgeDatabase>();

    public MEVD.VectorStore Store => _services.GetRequiredService<MEVD.VectorStore>();

    public ILoggerFactory LoggerFactory => _services.GetRequiredService<ILoggerFactory>();

    /// <summary>The ingestion collection, as SP3's own runner opens it.</summary>
    public MEVD.VectorStoreCollection<object, Dictionary<string, object?>> Collection =>
        Store.GetDynamicCollection(
            "chunks",
            IngestionSchema.BuildDefinition(384, MEVD.DistanceFunction.CosineDistance, fullTextIndexed: true));

    public ResolvedChunkOptions ResolvedChunking(Action<ChunkOptions>? configure = null)
    {
        var options = new ChunkOptions();
        configure?.Invoke(options);
        return options.Resolve(ChunkModelProfile.MiniLmL6V2Int8, Tokenizer);
    }

    public static async Task<MediTestHost> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-medi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var generator = new RecordingEmbeddingGenerator();
        var tokenizer = new WhitespaceTokenizer();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));
        services.AddQavrenEdge(edge =>
        {
            edge.UseSqliteNative();
            edge.AddSqlite(o =>
            {
                o.DatabaseName = "test.db";
                o.Directory = root;
            });
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(tokenizer);
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
            edge.AddIngestion(1);
        });

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IEdgeHost>()
            .EnsureStartedAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return new MediTestHost(provider, root, generator);
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory that will not delete is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FixedPaths : IEdgePaths
    {
        public FixedPaths(string root)
        {
            Data = root;
            Cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Cache);
        }

        public string Data { get; }

        public string Cache { get; }
    }
}
