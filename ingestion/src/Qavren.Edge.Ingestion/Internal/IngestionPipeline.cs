using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// The <see cref="IIngestionPipeline"/> implementation. It resolves per-collection dependencies at
/// call time and refuses an unknown collection name before any enumeration, any open and any write.
/// </summary>
internal sealed class IngestionPipeline : IIngestionPipeline
{
    private readonly IServiceProvider _services;
    private readonly IngestionRegistry _registry;
    private readonly IngestionRunControl _control;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lock _gate = new();
    private string? _activeRunId;
    private IngestionRunner? _active;

    public IngestionPipeline(
        IServiceProvider services,
        IngestionRegistry registry,
        IngestionRunControl control,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(control);

        _services = services;
        _registry = registry;
        _control = control;
        _time = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public IngestionProgress? Current => _active?.Current;

    /// <inheritdoc />
    public void RequestStop(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _control.RequestStop(reason);
    }

    /// <inheritdoc />
    public Task<IngestionRunResult> RunAsync(
        IngestionSource source,
        string? collectionName = null,
        IngestionRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 6002 FIRST: before any enumeration, any open and any write.
        var registration = _registry.Resolve(collectionName);
        return RunCoreAsync(registration, source, options ?? new IngestionRunOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IngestionRunResult> RunAsync(
        IngestionSource source,
        IProgress<IngestionProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        return RunAsync(source, collectionName: null, new IngestionRunOptions { Progress = progress }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(
        IngestionSource source, string? collectionName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var runner = Build(_registry.Resolve(collectionName));
        return await runner.PruneAsync(source, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> RemoveSourceAsync(
        string sourceId, string? collectionName = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var runner = Build(_registry.Resolve(collectionName));
        return await runner.RemoveSourceAsync(sourceId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> RemoveDocumentAsync(
        string sourceId, string documentId, string? collectionName = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        var runner = Build(_registry.Resolve(collectionName));
        return await runner.RemoveDocumentAsync(sourceId, documentId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IngestionStatus> GetStatusAsync(
        string? collectionName = null, CancellationToken ct = default)
    {
        var registration = _registry.Resolve(collectionName);
        var recipe = RequireRecipe(registration);
        var database = Database(registration);
        var state = new IngestionStateStore(database, registration.Options.StateTablePrefix);
        var latest = await state.LatestRunAsync(registration.CollectionName, ct).ConfigureAwait(false);
        var extractors = IngestionServiceResolution.BuildRegistry(_services, registration.Options);
        var counts = await state.CountAsync(
            registration.CollectionName,
            RecipeComposition.CurrentHashes(recipe, extractors),
            latest?.RunId,
            ct).ConfigureAwait(false);
        var chunks = await state.ChunkCountAsync(registration.CollectionName, ct).ConfigureAwait(false);

        return new IngestionStatus(
            registration.CollectionName,
            recipe.Hash,
            counts.Documents,
            counts.Indexed,
            counts.Failed,
            counts.NoTextLayer,
            counts.Stale,
            counts.RecipeStale,
            chunks,
            latest?.RunId,
            latest?.Outcome,
            latest?.SuspendReason,
            latest?.StartedUtc);
    }

    /// <inheritdoc />
    public ValueTask<IngestionRecipe> GetRecipeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RequireRecipe(_registry.Resolve(null)));
    }

    private async Task<IngestionRunResult> RunCoreAsync(
        IngestionRegistration registration,
        IngestionSource source,
        IngestionRunOptions options,
        CancellationToken cancellationToken)
    {
        var runner = Build(registration);

        // The id is minted HERE, before the gate closes, so 6008 can name the run that is actually
        // active. Reading it back off the runner would race: Current is null until the run's first
        // progress report, which is after the concurrent caller has already been refused.
        var runId = IngestionRunner.NewRunId();

        lock (_gate)
        {
            if (_activeRunId is { } active)
            {
                throw new EdgeIngestionException(
                    EdgeErrorCode.IngestionRunAlreadyActive,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Ingestion run {0} is already active on this pipeline. Runs are sequential.",
                        active))
                {
                    RunId = active,
                    Remediation = "Await the active run, or call RequestStop before starting another.",
                };
            }

            _activeRunId = runId;
            _active = runner;
        }

        try
        {
            return await runner.RunAsync(source, options, runId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _activeRunId = null;
                _active = null;
            }
        }
    }

    private IngestionRunner Build(IngestionRegistration registration)
    {
        var options = registration.Options;
        var database = Database(registration);
        var store = Store(options);
        var collection = store.GetDynamicCollection(options.CollectionName, registration.Definition);
        var schema = (EdgeVectorSchema?)collection.GetService(typeof(EdgeVectorSchema));
        var generator = RequireGenerator(options);
        var tokenizer = registration.Tokenizer ?? RequireTokenizer();
        var throttle = _services.GetService<IIngestionThrottle>() ?? new FixedIngestionThrottle();
        var extractors = IngestionServiceResolution.BuildRegistry(_services, options);
        var logger = _loggerFactory.CreateLogger("Qavren.Edge.Ingestion");
        var dataTable = schema?.DataTable ?? options.CollectionName;

        var writer = new ChunkWriter(database, collection, generator, tokenizer, dataTable, logger);
        var state = new IngestionStateStore(database, options.StateTablePrefix);

        return new IngestionRunner(
            registration, state, database, writer, tokenizer, throttle, extractors,
            _control, _time, logger, dataTable);
    }

    private IEdgeDatabase Database(IngestionRegistration registration) =>
        registration.Options.DatabaseName is { } name
            ? _services.GetRequiredKeyedService<IEdgeDatabase>(name)
            : _services.GetRequiredService<IEdgeDatabase>();

    private MEVD.VectorStore Store(IngestionOptions options) =>
        options.StoreName is { } name
            ? _services.GetRequiredKeyedService<MEVD.VectorStore>(name)
            : _services.GetRequiredService<MEVD.VectorStore>();

    private IEmbeddingGenerator<string, Embedding<float>> RequireGenerator(IngestionOptions options) =>
        IngestionServiceResolution.FindGenerator(_services, options.StoreName)
        ?? throw IngestionServiceResolution.GeneratorMissing(options.StoreName);

    private IChunkTokenizer RequireTokenizer() =>
        _services.GetService<IChunkTokenizer>() ?? throw IngestionServiceResolution.TokenizerMissing();

    private static IngestionRecipe RequireRecipe(IngestionRegistration registration) =>
        registration.Recipe
        ?? throw new EdgeIngestionException(
            EdgeErrorCode.TokenCounterMissing,
            "The ingestion recipe has not been frozen. The order-400 startup task did not run.")
        {
            Remediation = "Start the Qavren.Edge host (EdgeHost/AddEdge) before using the pipeline.",
        };
}

/// <summary>The "resolve or explain" helpers the pipeline and the startup task share.</summary>
internal static class IngestionServiceResolution
{
    /// <summary>
    /// Spec 7.1's registry, composed <b>at resolve time</b> from BOTH consumer channels plus the
    /// built-ins: everything registered through <c>AddDocumentExtractor</c> (which is what
    /// <c>AddPdfExtractor</c> and <c>AddDocxExtractor</c> call, and why they are order-independent
    /// against <c>AddIngestion</c>), then <see cref="IngestionOptions.Extractors"/>, then text and
    /// markdown. Composing it in a factory that discards the <see cref="IServiceProvider"/> would
    /// make both <c>AddDocumentExtractor</c> overloads dead registrations and send every PDF back
    /// as 6101 Unsupported.
    /// </summary>
    public static DocumentExtractorRegistry BuildRegistry(IServiceProvider services, IngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return new DocumentExtractorRegistry(
            [.. services.GetServices<IDocumentExtractor>(), .. options.Extractors]);
    }

    /// <summary>
    /// The unkeyed <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>, or the one keyed on
    /// <c>StoreName</c>. Resolved with <c>GetService</c>, matching how SP2's store factory resolves
    /// its own; its absence is 6208 at START time, not a null-reference on the first document.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>>? FindGenerator(
        IServiceProvider services, string? storeName)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (storeName is not null)
        {
            var keyed = services.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(storeName);
            if (keyed is not null)
            {
                return keyed;
            }
        }

        return services.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
    }

    public static EdgeConfigurationException GeneratorMissing(string? storeName) =>
        new(
            EdgeErrorCode.IngestionEmbeddingGeneratorMissing,
            string.Format(
                CultureInfo.InvariantCulture,
                "No IEmbeddingGenerator<string, Embedding<float>> is registered{0}. Qavren.Edge.Ingestion calls the " +
                "generator itself (spec 9.5 step a1), so its absence is a start-time fact. Call AddOnnxEmbeddings(), " +
                "or register any IEmbeddingGenerator<string, Embedding<float>>.",
                storeName is null ? string.Empty : $" under the key '{storeName}' or unkeyed"));

    public static EdgeConfigurationException TokenizerMissing() =>
        new(
            EdgeErrorCode.TokenCounterMissing,
            "No IChunkTokenizer is registered. Chunking is budgeted in TOKENS and there is deliberately no chars/4 " +
            "fallback - that is the prior art's hidden-truncation bug. Call AddOnnxIngestion() to bridge SP2's " +
            "tokenizer, or UseChunkTokenizer() to supply your own (EdgeTokenCounter.CreateWordPiece builds one from " +
            "a vocab.txt).");
}
