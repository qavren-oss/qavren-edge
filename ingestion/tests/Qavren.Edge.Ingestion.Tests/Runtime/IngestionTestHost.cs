using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// A fully wired Qavren.Edge host over a scratch directory, with SP2's store and SP3's ingestion
/// registered against real <c>vec0</c> and FTS5. Near-unit rather than mocked: the runtime suite is
/// about who calls what in which order, and a mocked database cannot answer that.
/// </summary>
public sealed class IngestionTestHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _root;
    private readonly bool _ownsRoot;

    private IngestionTestHost(ServiceProvider services, string root, bool ownsRoot)
    {
        _services = services;
        _root = root;
        _ownsRoot = ownsRoot;
    }

    public IServiceProvider Services => _services;

    public IIngestionPipeline Pipeline => _services.GetRequiredService<IIngestionPipeline>();

    public IEdgeDatabase Database => _services.GetRequiredService<IEdgeDatabase>();

    public RecordingEmbeddingGenerator Generator { get; private init; } = null!;

    public ManualTimeProvider Time { get; private init; } = null!;

    public CapturingLoggerProvider Logs { get; private init; } = null!;

    internal IngestionStateStore State(string prefix = "qedge_ingest") => new(Database, prefix);

    /// <summary>Builds the container without starting the host. Startup faults surface at Start.</summary>
    public static ServiceProvider Build(
        Action<EdgeBuilder>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "qedge-ingestion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

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
            configure?.Invoke(edge);
        });

        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>The canonical wiring: SQLite, a vector store, ingestion, a tokenizer, a generator.</summary>
    public static async Task<IngestionTestHost> StartAsync(
        Action<IngestionOptions>? configureIngestion = null,
        Action<EdgeVectorStoreOptions>? configureStore = null,
        Action<EdgeBuilder>? configure = null,
        int migrationVersion = 1,
        RecordingEmbeddingGenerator? configureGenerator = null,
        string? root = null,
        Action<EdgeBuilder>? configureBeforeIngestion = null,
        bool useSystemTime = false)
    {
        // A caller-supplied root is kept on Dispose, so two hosts can be started over one database
        // - which is how a CONFIGURATION change (a second AddIngestion with an extra extractor) can
        // be asserted against state the first configuration wrote.
        var ownsRoot = root is null;
        root ??= Path.Combine(Path.GetTempPath(), "qedge-ingestion-tests", Guid.NewGuid().ToString("N"));
        var generator = configureGenerator ?? new RecordingEmbeddingGenerator();
        var time = new ManualTimeProvider();
        var logs = new CapturingLoggerProvider();

        var provider = Build(
            edge =>
            {
                edge.AddVectorStore(configureStore);
                edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
                edge.Services.AddSingleton<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(generator);
                configureBeforeIngestion?.Invoke(edge);
                edge.AddIngestion(migrationVersion, configureIngestion);
                configure?.Invoke(edge);
            },
            services =>
            {
                // A test that exercises a real DELAY needs a real clock: Task.Delay against a
                // ManualTimeProvider nobody advances never completes.
                services.AddSingleton(useSystemTime ? TimeProvider.System : time);
                services.AddLogging(b => b.AddProvider(logs));
            },
            root);

        await provider.GetRequiredService<IEdgeHost>()
            .EnsureStartedAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return new IngestionTestHost(provider, root, ownsRoot)
        {
            Generator = generator,
            Time = time,
            Logs = logs,
        };
    }

    public void Dispose()
    {
        _services.Dispose();
        if (!_ownsRoot)
        {
            return;
        }

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
