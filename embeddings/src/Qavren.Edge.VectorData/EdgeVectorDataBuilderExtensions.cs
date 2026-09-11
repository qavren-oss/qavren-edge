using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Internal;
using Qavren.Edge.VectorData.Internal;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData;

/// <summary>Registers the vector store, its collections and their schema on the Qavren.Edge builder.</summary>
public static class EdgeVectorDataBuilderExtensions
{
    private const string TrimMessage =
        "Reflects over TRecord. Use the VectorStoreCollectionDefinition overload in trimmed or AOT apps.";

    /// <summary>The startup order of the ad-hoc schema task, per spec 14.1.</summary>
    private const int VectorSchemaStartupOrder = 300;

    /// <summary>
    /// Registers <see cref="EdgeVectorStore"/> and MEVD's <c>VectorStore</c> over the named
    /// sub-project 1 database, plus the diagnostics contributor and the lifecycle observer.
    /// <para>
    /// <b>Embedding generator resolution, which is what makes the four-call path work.</b> The
    /// registered factory resolves <see cref="IEmbeddingGenerator"/> from the container with
    /// <c>GetService</c> - <b>never</b> <c>GetRequiredService</c>, because a store used only with
    /// pre-computed <see cref="ReadOnlyMemory{T}"/> vectors needs no generator and demanding one
    /// would break that. The result goes to <see cref="EdgeVectorStore"/>'s
    /// <c>embeddingGenerator</c> parameter, which applies it only where
    /// <see cref="EdgeVectorStoreOptions.EmbeddingGenerator"/> is still null.
    /// </para>
    /// <para>
    /// The query generator is then resolved <b>from the generator itself, by service key</b>, with
    /// no reference to <c>Qavren.Edge.Embeddings.Onnx</c>: the key is this package's own
    /// <see cref="EdgeVectorData.QueryGeneratorServiceKey"/>, the call is <c>GetService</c> rather
    /// than the ONNX package's <c>AsQueryGenerator()</c> convenience, and it asks for the
    /// <b>non-generic</b> <see cref="IEmbeddingGenerator"/> - a third-party generator may be typed
    /// <c>IEmbeddingGenerator&lt;DataContent, Embedding&lt;float&gt;&gt;</c>, and asking for a
    /// closed generic it does not implement would return null and quietly lose the query lane.
    /// </para>
    /// <para>
    /// Builder-call order does not matter, because the factory runs at <b>resolve</b> time.
    /// </para>
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Configures the store options.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddVectorStore(this EdgeBuilder builder, Action<EdgeVectorStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RegisterShared(builder);

        builder.Services.AddSingleton(sp => CreateStore(sp, storeName: null, configure));
        builder.Services.AddSingleton<MEVD.VectorStore>(sp => sp.GetRequiredService<EdgeVectorStore>());
        return builder;
    }

    /// <summary>
    /// Keyed variant. Resolves the <b>keyed</b> <see cref="IEmbeddingGenerator"/> under the same
    /// <paramref name="name"/> first, then the unkeyed one, so a named store pairs with a named
    /// generator by convention.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="name">The service key the store registers under.</param>
    /// <param name="configure">Configures the store options.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddVectorStore(
        this EdgeBuilder builder,
        string name,
        Action<EdgeVectorStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        RegisterShared(builder);

        builder.Services.AddKeyedSingleton(name, (sp, key) => CreateStore(sp, (string)key!, configure));
        builder.Services.AddKeyedSingleton<MEVD.VectorStore>(
            name,
            (sp, key) => sp.GetRequiredKeyedService<EdgeVectorStore>(key));
        return builder;
    }

    /// <summary>
    /// RECOMMENDED. Emits the collection DDL as a sub-project 1 <c>IEdgeMigration</c>, so the
    /// schema is versioned by the existing migrator at startup order 100 with
    /// <c>PRAGMA user_version</c> bookkeeping instead of appearing on first use.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="version">The migration version, unique and ascending across the database.</param>
    /// <param name="collectionName">The collection name; the data table is named after it verbatim.</param>
    /// <param name="databaseName">The sub-project 1 database name. Null is the unnamed database.</param>
    /// <param name="configure">Configures the per-collection overrides.</param>
    /// <returns>The builder, for chaining.</returns>
    [RequiresDynamicCode(TrimMessage)]
    [RequiresUnreferencedCode(TrimMessage)]
    public static EdgeBuilder AddVectorCollectionMigration<TKey, TRecord>(
        this EdgeBuilder builder,
        int version,
        string collectionName,
        string? databaseName = null,
        Action<EdgeVectorStoreCollectionOptions>? configure = null)
        where TKey : notnull
        where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var options = Configure(configure);

        // The container does not exist yet, so the app's real generator is out of reach - and this
        // model is used for nothing but DDL, which a generator contributes nothing to. See
        // SchemaOnlyEmbeddingGenerator.
        var model = new EdgeCollectionModelBuilder(collectionName).Build(
            typeof(TRecord),
            typeof(TKey),
            options.Definition,
            options.EmbeddingGenerator ?? SchemaOnlyEmbeddingGenerator.Instance);

        return AddMigrationCore(builder, version, collectionName, databaseName, model, options, typeof(TKey));
    }

    /// <summary>
    /// The trim/AOT-safe variant: the model is built from the definition with
    /// <c>CollectionModelBuilder.BuildDynamic</c>, which reflects over nothing.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="version">The migration version, unique and ascending across the database.</param>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="definition">The record shape.</param>
    /// <param name="databaseName">The sub-project 1 database name. Null is the unnamed database.</param>
    /// <param name="configure">Configures the per-collection overrides.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddVectorCollectionMigration(
        this EdgeBuilder builder,
        int version,
        string collectionName,
        MEVD.VectorStoreCollectionDefinition definition,
        string? databaseName = null,
        Action<EdgeVectorStoreCollectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var options = Configure(configure);
        options.Definition ??= definition;

        var model = new EdgeCollectionModelBuilder(collectionName)
            .BuildDynamic(options.Definition, options.EmbeddingGenerator ?? SchemaOnlyEmbeddingGenerator.Instance);

        return AddMigrationCore(builder, version, collectionName, databaseName, model, options, typeof(object));
    }

    /// <summary>
    /// The ad-hoc alternative: an <c>IEdgeStartupTask</c> at order 300 calling
    /// <c>EnsureCollectionExistsAsync</c>. Same SQL, no version bookkeeping.
    /// <para>
    /// <b>Known limitation, unresolved as of 2026-09-11.</b> A task running inside the startup
    /// sequence cannot open a database connection: <c>IEdgeDatabase.OpenConnectionAsync</c> awaits
    /// <c>IEdgeHost.EnsureStartedAsync</c>, and that barrier only lifts once every startup task -
    /// including this one - has returned, so the host deadlocks. Sub-project 1 solves this for its
    /// own order-10 and order-100 tasks with an <c>internal</c> <c>EdgeDatabase.OpenCoreAsync</c>,
    /// which this package cannot reach. Until sub-project 1 exposes a startup-safe open, use
    /// <see cref="AddVectorCollectionMigration{TKey, TRecord}"/> (the recommended path anyway) or
    /// call <c>EnsureCollectionExistsAsync</c> yourself after the host has started.
    /// </para>
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="configure">Configures the per-collection overrides.</param>
    /// <param name="storeName">The keyed store to resolve the database from. Null is the unnamed store.</param>
    /// <returns>The builder, for chaining.</returns>
    [RequiresDynamicCode(TrimMessage)]
    [RequiresUnreferencedCode(TrimMessage)]
    public static EdgeBuilder AddVectorCollection<TKey, TRecord>(
        this EdgeBuilder builder,
        string collectionName,
        Action<EdgeVectorStoreCollectionOptions>? configure = null,
        string? storeName = null)
        where TKey : notnull
        where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        RegisterShared(builder);

        builder.Services.AddSingleton<IEdgeStartupTask>(sp => new VectorSchemaStartupTask(token =>
        {
            var store = storeName is null
                ? sp.GetRequiredService<EdgeVectorStore>()
                : sp.GetRequiredKeyedService<EdgeVectorStore>(storeName);

            var database = (IEdgeDatabase)store.GetService(typeof(IEdgeDatabase))!;
            var options = Configure(configure);
            options.EmbeddingGenerator ??= sp.GetService<IEmbeddingGenerator>();

            return new EdgeVectorStoreCollection<TKey, TRecord>(database, collectionName, options)
                .EnsureCollectionExistsAsync(token);
        }));

        return builder;
    }

    private static EdgeVectorStoreCollectionOptions Configure(Action<EdgeVectorStoreCollectionOptions>? configure)
    {
        var options = new EdgeVectorStoreCollectionOptions();
        configure?.Invoke(options);
        return options;
    }

    private static EdgeBuilder AddMigrationCore(
        EdgeBuilder builder,
        int version,
        string collectionName,
        string? databaseName,
        CollectionModel model,
        EdgeVectorStoreCollectionOptions options,
        Type keyType)
    {
        RegisterShared(builder);

        var schema = EdgeVectorCollectionSchemaFactory.Create(model, collectionName, options);
        var key = databaseName ?? SqliteRegistry.DefaultName;

        builder.AddMigrations(
            [new EdgeVectorCollectionMigration(version, collectionName, schema.BuildCreateSql())],
            databaseName);

        Registry(builder).Add(new EdgeVectorCollectionRegistration(
            key,
            collectionName,
            schema.DataTable,
            schema.VectorTable,
            schema.FullTextTable,
            schema.Dimensions,
            model.VectorProperty.DistanceFunction ?? MEVD.DistanceFunction.CosineDistance,
            options.ChunkSize ?? new EdgeVectorStoreOptions().ChunkSize,
            (options.FullTextTokenizer ?? new EdgeVectorStoreOptions().FullTextTokenizer).ToString(),
            keyType.Name,
            options.EmbeddingGenerator?.GetType().Name,
            (options.EmbeddingGenerator?.GetService(typeof(EmbeddingGeneratorMetadata))
                as EmbeddingGeneratorMetadata)?.DefaultModelDimensions,
            (options.Rrf ?? new RrfDefaults()).K,
            (options.Rrf ?? new RrfDefaults()).VectorWeight,
            (options.Rrf ?? new RrfDefaults()).KeywordWeight));

        return builder;
    }

    private static EdgeVectorStore CreateStore(
        IServiceProvider services,
        string? storeName,
        Action<EdgeVectorStoreOptions>? configure)
    {
        var options = new EdgeVectorStoreOptions();
        configure?.Invoke(options);

        var database = options.DatabaseName is { } name
            ? services.GetRequiredKeyedService<IEdgeDatabase>(name)
            : services.GetRequiredService<IEdgeDatabase>();

        // GetService, never GetRequiredService: a store used only with pre-computed vectors needs
        // no generator at all, and demanding one would break that.
        var generator = storeName is null
            ? services.GetService<IEmbeddingGenerator>()
            : services.GetKeyedService<IEmbeddingGenerator>(storeName) ?? services.GetService<IEmbeddingGenerator>();

        // Asked of the generator, by this package's own key, with no reference to the ONNX package.
        var query = generator?.GetService(typeof(IEmbeddingGenerator), EdgeVectorData.QueryGeneratorServiceKey)
            as IEmbeddingGenerator
            ?? generator;

        services.GetRequiredService<EdgeVectorCollectionRegistry>().IncludeRowCounts =
            options.IncludeRowCountsInDiagnostics;

        return new EdgeVectorStore(
            database,
            options,
            generator,
            query,
            services.GetService<ILoggerFactory>());
    }

    private static void RegisterShared(EdgeBuilder builder)
    {
        _ = Registry(builder);

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeDiagnosticsContributor, VectorDataDiagnosticsContributor>());

        // Sub-project 1's SqliteLifecycleObserver checkpoints WAL on the same Sleeping event, and
        // the FTS merge has to run FIRST so its pages land in that checkpoint. AddSqlite is
        // normally called before AddVectorStore, so appending with TryAddEnumerable would put this
        // observer second; inserting at the head puts the merge where spec 14.2 needs it, and the
        // guard keeps the registration idempotent exactly as TryAddEnumerable would.
        if (!builder.Services.Any(d =>
                d.ServiceType == typeof(IEdgeLifecycleObserver)
                && d.ImplementationType == typeof(VectorDataLifecycleObserver)))
        {
            builder.Services.Insert(
                0,
                ServiceDescriptor.Singleton<IEdgeLifecycleObserver, VectorDataLifecycleObserver>());
        }
    }

    private static EdgeVectorCollectionRegistry Registry(EdgeBuilder builder)
    {
        var existing = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(EdgeVectorCollectionRegistry))?
            .ImplementationInstance as EdgeVectorCollectionRegistry;
        if (existing is not null)
        {
            return existing;
        }

        var registry = new EdgeVectorCollectionRegistry();
        builder.Services.AddSingleton(registry);
        return registry;
    }

    /// <summary>The ad-hoc schema task. Order 300, after migrations and after the database is open.</summary>
    private sealed class VectorSchemaStartupTask(Func<CancellationToken, Task> run) : IEdgeStartupTask
    {
        public int Order => VectorSchemaStartupOrder;

        public Task RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
    }
}
