using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData;

namespace Qavren.Edge.Ingestion;

/// <summary>The four builder calls <c>Qavren.Edge.Ingestion</c> publishes.</summary>
public static partial class IngestionEdgeBuilderExtensions
{
    /// <summary>
    /// The one call. It registers <b>two</b> migrations — <paramref name="migrationVersion"/> for
    /// the chunk collection, through SP2's own <c>AddVectorCollectionMigration</c>, and
    /// <paramref name="migrationVersion"/> + 1 for the three ingestion-state tables — plus the
    /// pipeline, the state store, the extractor registry (text + markdown), the chunkers, the
    /// lifecycle observer, the diagnostics contributor, and one order-400 startup task that opens
    /// NO database.
    /// <para>
    /// <b>Both versions are claimed.</b> They must be unique and ascending across the whole
    /// database: <c>PRAGMA user_version</c> is one counter, so a second migration at
    /// <paramref name="migrationVersion"/> + 1 elsewhere is
    /// <see cref="EdgeErrorCode.MigrationVersionConflict"/> from SP1's registry.
    /// </para>
    /// <para>
    /// Idempotent per collection; a second call for the same collection throws
    /// <see cref="EdgeErrorCode.IngestionMigrationVersionConflict"/> (6007).
    /// </para>
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="migrationVersion">The first of the two versions this call claims.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddIngestion(
        this EdgeBuilder builder, int migrationVersion, Action<IngestionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new IngestionOptions();
        configure?.Invoke(options);

        // ---- 1. Validate every TOKENIZER-INDEPENDENT option. ResolvedChunkOptions is NOT resolved
        //         here: that needs an IChunkTokenizer for SpecialTokenOverhead and the
        //         DocumentPrefix reserve, no tokenizer exists at builder time, and
        //         AddOnnxIngestion() has not even been chained yet.
        Validate(options);

        // ---- 2. The definition, programmatically. No attributed record type, no reflection.
        var dimensions = options.Dimensions ?? options.Model.Dimensions;
        var definition = IngestionSchema.BuildDefinition(
            dimensions, options.DistanceFunction, options.FullTextIndexed);

        var collectionOptions = new EdgeVectorStoreCollectionOptions { Definition = definition };
        options.ConfigureCollection?.Invoke(collectionOptions);
        collectionOptions.Definition ??= definition;

        var registry = Registry(builder);
        registry.Add(new IngestionRegistration(options, definition, collectionOptions, migrationVersion));

        // ---- 3. Two migrations. The collection goes through SP2's own path, which also enters
        //         SP3's collection in EdgeVectorCollectionRegistry - which is what lets SP2's
        //         bounded FTS5 merge cover this sidecar (plan adjustment 2).
        builder.AddVectorCollectionMigration(
            migrationVersion,
            options.CollectionName,
            definition,
            options.DatabaseName,
            o =>
            {
                o.Definition = collectionOptions.Definition;
                o.VectorTableName = collectionOptions.VectorTableName;
                o.FullTextTableName = collectionOptions.FullTextTableName;
                o.ChunkSize = collectionOptions.ChunkSize;
                o.FullTextTokenizer = collectionOptions.FullTextTokenizer;
                o.KeywordCombinator = collectionOptions.KeywordCombinator;
                o.Rrf = collectionOptions.Rrf;
                o.AlwaysCreateFullTextIndex = collectionOptions.AlwaysCreateFullTextIndex;
            });

        builder.AddMigrations(
            [new IngestionStateMigration(migrationVersion + 1, options.StateTablePrefix)],
            options.DatabaseName);

        // ---- 4. TryAdd everything else. Every registration is guarded, because SP2 has two paths
        //         that are not and SP3 must not add a third.
        builder.Services.TryAddSingleton<IngestionRunControl>();
        builder.Services.TryAddSingleton<IIngestionPipeline>(sp => new IngestionPipeline(
            sp,
            sp.GetRequiredService<IngestionRegistry>(),
            sp.GetRequiredService<IngestionRunControl>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        // Composed at RESOLVE time from sp.GetServices<IDocumentExtractor>() plus options.Extractors,
        // which is what makes both AddDocumentExtractor overloads live registrations and what makes
        // AddPdfExtractor / AddDocxExtractor order-independent against this call.
        builder.Services.TryAddSingleton<IDocumentExtractorRegistry>(
            sp => IngestionServiceResolution.BuildRegistry(sp, options));
        builder.Services.TryAddSingleton<IIngestionThrottle>(_ => new FixedIngestionThrottle());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeDiagnosticsContributor, IngestionDiagnosticsContributor>());

        // The lifecycle observer is the ONE exception, and it is not TryAddEnumerable: spec 12
        // requires it to run BEFORE SP2's and SP1's observers, and TryAddEnumerable appends.
        // Inserting the descriptor at index 0 is the same idiom SP2's VectorDataLifecycleObserver
        // uses to get ahead of SP1's; the descriptor scan is what keeps AddIngestion idempotent.
        if (!builder.Services.Any(d =>
                d.ServiceType == typeof(IEdgeLifecycleObserver)
                && d.ImplementationType == typeof(IngestionLifecycleObserver)))
        {
            builder.Services.Insert(
                0, ServiceDescriptor.Singleton<IEdgeLifecycleObserver, IngestionLifecycleObserver>());
        }

        // ---- 5. One order-400 startup task that touches NO database.
        if (!builder.Services.Any(d =>
                d.ServiceType == typeof(IEdgeStartupTask)
                && d.ImplementationType == typeof(IngestionValidateStartupTask)))
        {
            builder.Services.AddSingleton<IEdgeStartupTask, IngestionValidateStartupTask>();
        }

        return builder;
    }

    /// <summary>
    /// Registers one consumer <see cref="IDocumentExtractor"/>, ahead of the built-ins. The registry
    /// is composed at RESOLVE time, so this may be called before or after
    /// <see cref="AddIngestion(EdgeBuilder, int, Action{IngestionOptions}?)"/> — which is what makes
    /// <c>AddPdfExtractor()</c> and <c>AddDocxExtractor()</c> order-independent.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="extractor">The extractor.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddDocumentExtractor(this EdgeBuilder builder, IDocumentExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(extractor);

        builder.Services.AddSingleton(extractor);
        return builder;
    }

    /// <summary>The factory overload, for an extractor that needs services.</summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="factory">Builds the extractor.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddDocumentExtractor(
        this EdgeBuilder builder, Func<IServiceProvider, IDocumentExtractor> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Services.AddSingleton(factory);
        return builder;
    }

    /// <summary>
    /// Supplies the <see cref="IChunkTokenizer"/> without the ONNX satellite —
    /// <c>EdgeTokenCounter.CreateWordPiece</c> builds one from a <c>vocab.txt</c>.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="factory">Builds the tokenizer.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder UseChunkTokenizer(
        this EdgeBuilder builder, Func<IServiceProvider, IChunkTokenizer> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Services.AddSingleton(factory);
        return builder;
    }

    /// <summary>Replaces <see cref="FixedIngestionThrottle"/> with a consumer's policy.</summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="factory">Builds the throttle.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder UseIngestionThrottle(
        this EdgeBuilder builder, Func<IServiceProvider, IIngestionThrottle> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Services.AddSingleton(factory);
        return builder;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex StateTablePrefixPattern { get; }

    private static void Validate(IngestionOptions options)
    {
        if (options.Chunking.OverlapTokens is { } overlap
            && options.Chunking.MaxTokens is { } max
            && overlap >= max / 2)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "ChunkOptions.OverlapTokens is {0}; it must be below MaxTokens / 2 = {1}.",
                overlap,
                max / 2));
        }

        if (options.Chunking.MinTokens is { } min && options.Chunking.MaxTokens is { } ceiling && min >= ceiling)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "ChunkOptions.MinTokens is {0}; it must be below MaxTokens = {1}.",
                min,
                ceiling));
        }

        if (options.Chunking.HeadingPathTokenBudget <= 0)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "ChunkOptions.HeadingPathTokenBudget is {0}; it must be positive.",
                options.Chunking.HeadingPathTokenBudget));
        }

        if (options.WriteBatchSize <= 0)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "IngestionOptions.WriteBatchSize is {0}; it must be positive.",
                options.WriteBatchSize));
        }

        if (options.DeleteBatchSize <= 0)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "IngestionOptions.DeleteBatchSize is {0}; it must be positive.",
                options.DeleteBatchSize));
        }

        if (options.SleepGraceBudget > TimeSpan.FromSeconds(2))
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "IngestionOptions.SleepGraceBudget is {0}; it must be at most 2 s. SP1's platform bridges raise " +
                "Sleeping on the callback thread, against iOS's ~5 s window and Android's OnPause ANR path.",
                options.SleepGraceBudget));
        }

        if (options.FullTextRemoveDiacritics is < 0 or > 2)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "IngestionOptions.FullTextRemoveDiacritics is {0}; FTS5 accepts 0, 1 or 2.",
                options.FullTextRemoveDiacritics));
        }

        if (!StateTablePrefixPattern.IsMatch(options.StateTablePrefix))
        {
            // The state DDL is the ONE place in SP3 where a consumer string reaches an identifier
            // position, so it is validated here rather than quoted and hoped for.
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "IngestionOptions.StateTablePrefix '{0}' is not a bare SQL identifier (^[A-Za-z_][A-Za-z0-9_]*$).",
                options.StateTablePrefix));
        }
    }

    private static EdgeConfigurationException Invalid(string message) =>
        new(EdgeErrorCode.IngestionOptionsInvalid, message);

    private static IngestionRegistry Registry(EdgeBuilder builder)
    {
        var existing = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(IngestionRegistry))?
            .ImplementationInstance as IngestionRegistry;
        if (existing is not null)
        {
            return existing;
        }

        var registry = new IngestionRegistry();
        builder.Services.AddSingleton(registry);
        return registry;
    }
}
