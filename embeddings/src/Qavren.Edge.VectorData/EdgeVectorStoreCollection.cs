using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData.Internal;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData;

/// <summary>
/// One collection: a data table, a vec0 sidecar, an optional FTS5 sidecar, and the triggers that
/// keep the three in step. Every statement it runs is text <see cref="EdgeVectorSchema"/> emits,
/// so a consumer can read, log or hand-execute it.
/// </summary>
/// <typeparam name="TKey">The key type: <c>int</c>, <c>long</c>, <c>string</c> or <c>Guid</c>.</typeparam>
/// <typeparam name="TRecord">The record type.</typeparam>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The name mirrors the MEVD base type VectorStoreCollection<TKey, TRecord> this derives from. Renaming it would break the one-to-one reading of the provider against the abstraction it implements.")]
public class EdgeVectorStoreCollection<TKey, TRecord>
    : MEVD.VectorStoreCollection<TKey, TRecord>, MEVD.IKeywordHybridSearchable<TRecord>
    where TKey : notnull
    where TRecord : class
{
    private const string TrimMessage =
        "Reflects over TRecord. Use GetDynamicCollection in trimmed or AOT apps.";

    private static readonly Action<ILogger, string, Exception?> LogCollectionCreated =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(800, "CollectionCreated"),
            "Vector collection '{Collection}' created.");

    private static readonly Action<ILogger, string, Exception?> LogCollectionDropped =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(801, "CollectionDropped"),
            "Vector collection '{Collection}' dropped.");

    private static readonly Action<ILogger, string, int, int, Exception?> LogHybridSearchExecuted =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Debug,
            new EventId(802, "HybridSearchExecuted"),
            "Hybrid search on '{Collection}' fused {Candidates} candidates per lane into {Results} results.");

    private readonly IEdgeDatabase _database;
    private readonly EdgeVectorStoreCollectionOptions _options;
    private readonly string[] _dataColumns;
    private readonly PropertyModel[] _dataProperties;

    /// <summary>
    /// Reflects over <typeparamref name="TRecord"/> through MEVD's reflection-based
    /// <c>CollectionModelBuilder.Build</c>, which is why it is annotated. The dynamic collection
    /// never reaches this constructor.
    /// </summary>
    /// <param name="database">The sub-project 1 database this collection lives in.</param>
    /// <param name="name">The collection name; the data table is named after it verbatim.</param>
    /// <param name="options">Per-collection overrides. Null takes every store default.</param>
    [RequiresDynamicCode(TrimMessage)]
    [RequiresUnreferencedCode(TrimMessage)]
    public EdgeVectorStoreCollection(
        IEdgeDatabase database,
        string name,
        EdgeVectorStoreCollectionOptions? options = null)
        // The ??= runs while the third argument is evaluated, so the fourth sees the same
        // instance. Two calls to a normalising helper would hand the base two different objects.
        : this(database, name, BuildReflectedModel(name, options ??= new EdgeVectorStoreCollectionOptions()), options)
    {
    }

    /// <summary>
    /// The trim/AOT-clean constructor, and the <b>only</b> one
    /// <see cref="EdgeDynamicVectorStoreCollection"/> chains to. It takes a model that is already
    /// built and carries no trim annotations, so a derived constructor chaining to it inherits
    /// neither IL2026 nor IL3050. Without it the "dynamic is the AOT-safe path" claim would be
    /// unverifiable: this repo sets <c>TreatWarningsAsErrors</c>, and the only way to compile a
    /// derived type over an annotated base constructor would be a suppression.
    /// </summary>
    /// <param name="database">The sub-project 1 database this collection lives in.</param>
    /// <param name="name">The collection name.</param>
    /// <param name="model">The already-built collection model.</param>
    /// <param name="options">Per-collection overrides. Never null.</param>
    protected EdgeVectorStoreCollection(
        IEdgeDatabase database,
        string name,
        CollectionModel model,
        EdgeVectorStoreCollectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        if (typeof(TKey) != typeof(object) && !SqliteTypeMap.IsSupportedKeyType(typeof(TKey)))
        {
            throw new EdgeVectorModelException(
                EdgeErrorCode.UnsupportedKeyType,
                name,
                model.KeyProperty.ModelName,
                $"Key type '{typeof(TKey)}' is not one this provider can store. Supported: {SqliteTypeMap.SupportedKeyTypes}.");
        }

        _database = database;
        _options = options;
        Name = name;
        Model = model;

        Schema = EdgeVectorCollectionSchemaFactory.Create(model, name, options);

        _dataProperties = [model.KeyProperty, .. model.DataProperties];
        _dataColumns = [.. _dataProperties.Select(p => p.StorageName)];
        Rrf = options.Rrf ?? new RrfDefaults();
        KeywordCombinator = options.KeywordCombinator ?? VectorData.KeywordCombinator.Or;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>The DDL and queries this collection runs, as plain strings.</summary>
    public EdgeVectorSchema Schema { get; }

    /// <summary>The built collection model.</summary>
    protected CollectionModel Model { get; }

    /// <summary>The reciprocal-rank-fusion defaults in force for this collection.</summary>
    protected RrfDefaults Rrf { get; }

    /// <summary>How this collection joins hybrid-search keywords by default.</summary>
    protected KeywordCombinator KeywordCombinator { get; }

    /// <summary>Set by <see cref="EdgeVectorStore"/> after construction; null for a hand-built collection.</summary>
    internal ILogger? Logger { get; set; }

    /// <inheritdoc />
    public override async Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                var count = await connection.ScalarAsync<long>(
                    "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name",
                    [new SqliteParameter("$name", Schema.DataTable)],
                    cancellationToken).ConfigureAwait(false);
                return count > 0;
            }
            catch (SqliteException ex)
            {
                throw Wrap(EdgeVectorStoreOperations.Get, ex);
            }
        }
    }

    /// <summary>
    /// Creates the data table, its indexes, the vec0 table, the FTS5 sidecar and all four triggers
    /// in one transaction. Before any of it: asserts the SQLite floor, then validates the
    /// configured generator's width against the declared vector width.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when every statement has been committed.</returns>
    public override async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        await AssertSqliteFloorAsync(cancellationToken).ConfigureAwait(false);
        AssertGeneratorDimensions();

        await RunStatementsAsync(
            Schema.BuildCreateSql(),
            EdgeVectorStoreOperations.CreateCollection,
            cancellationToken).ConfigureAwait(false);

        if (Logger is { } logger)
        {
            LogCollectionCreated(logger, Name, null);
        }
    }

    /// <inheritdoc />
    public override async Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        await RunStatementsAsync(
            Schema.BuildDropSql(),
            EdgeVectorStoreOperations.DeleteCollection,
            cancellationToken).ConfigureAwait(false);

        if (Logger is { } logger)
        {
            LogCollectionDropped(logger, Name, null);
        }
    }

    /// <inheritdoc />
    public override async Task<TRecord?> GetAsync(
        TKey key,
        MEVD.RecordRetrievalOptions? options = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var records = await ReadByKeysAsync([key], options?.IncludeVectors ?? false, cancellationToken)
            .ConfigureAwait(false);
        return records.Count == 0 ? null : records[0];
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<TRecord> GetAsync(
        IEnumerable<TKey> keys,
        MEVD.RecordRetrievalOptions? options = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var materialised = keys.ToArray();
        if (materialised.Length == 0)
        {
            yield break;
        }

        var records = await ReadByKeysAsync(materialised, options?.IncludeVectors ?? false, cancellationToken)
            .ConfigureAwait(false);
        foreach (var record in records)
        {
            yield return record;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<TRecord> GetAsync(
        Expression<Func<TRecord, bool>> filter,
        int top,
        MEVD.FilteredRecordRetrievalOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);

        var records = await ReadByFilterAsync(filter, top, options, cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            yield return record;
        }
    }

    /// <inheritdoc />
    public override Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return DeleteAsync([key], cancellationToken);
    }

    /// <summary>
    /// Deletes by key from the data table only: <c>DELETE FROM "notes" WHERE "Key" = $key</c>. The
    /// four triggers clean vec0 and FTS5, so no orphan survives even when an app deletes rows with
    /// its own SQL against the same <see cref="IEdgeDatabase"/>.
    /// </summary>
    /// <param name="keys">The keys to delete. A key that is not present is not an error.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the transaction has committed.</returns>
    public override async Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var materialised = keys.ToArray();
        if (materialised.Length == 0)
        {
            return;
        }

        var names = materialised.Select((_, i) => "$k" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var sql = $"DELETE FROM \"{Schema.DataTable}\" WHERE \"{Schema.KeyColumn}\" IN ({string.Join(",", names)})";
        var parameters = materialised.Select((k, i) => RecordMapper.Parameter(names[i], k)).ToArray();

        await ExecuteInTransactionAsync(
            EdgeVectorStoreOperations.Delete,
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(connection, transaction, sql, parameters, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return UpsertAsync([record], cancellationToken);
    }

    /// <summary>
    /// Overridden rather than inherited, because MEVD leaves the batch overload abstract with no
    /// default: every embedding in the batch is generated in <b>one</b> <c>GenerateAsync</c> call
    /// before the transaction opens, and the whole batch then commits together.
    /// <para>
    /// The vec0 side is delete-then-insert, never <c>UPDATE</c>, for two reasons both true of
    /// sqlite-vec 0.1.9: a working <c>INSERT OR REPLACE</c> only arrives in 0.1.10-alpha, and vec0
    /// overloads SQL <c>NULL</c> on a vector column to mean "no change". FTS5 is
    /// trigger-maintained off the data table and never appears in the write path.
    /// </para>
    /// </summary>
    /// <param name="records">The records to write.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the transaction has committed.</returns>
    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var materialised = records.ToArray();
        if (materialised.Length == 0)
        {
            return;
        }

        var vectors = await ResolveVectorsAsync(materialised, cancellationToken).ConfigureAwait(false);

        var upsertSql = Schema.BuildUpsertSql();
        var deleteVectorSql = $"DELETE FROM \"{Schema.VectorTable}\" WHERE rowid = $rowid";
        var insertVectorSql =
            $"INSERT INTO \"{Schema.VectorTable}\"(rowid, \"{Schema.VectorColumn}\") VALUES ($rowid, $embedding)";

        await ExecuteInTransactionAsync(
            EdgeVectorStoreOperations.Upsert,
            async (connection, transaction, token) =>
            {
                for (var i = 0; i < materialised.Length; i++)
                {
                    var record = materialised[i];
                    await AssignKeyIfGeneratedAsync(connection, transaction, record, token).ConfigureAwait(false);

                    var parameters = _dataProperties
                        .Select(p => RecordMapper.Parameter("$" + p.StorageName, p.GetValueAsObject(record)))
                        .ToArray();

                    var rowId = await ScalarAsync<long>(connection, transaction, upsertSql, parameters, token)
                        .ConfigureAwait(false);

                    await ExecuteAsync(
                        connection,
                        transaction,
                        deleteVectorSql,
                        [new SqliteParameter("$rowid", rowId)],
                        token).ConfigureAwait(false);

                    await ExecuteAsync(
                        connection,
                        transaction,
                        insertVectorSql,
                        [
                            new SqliteParameter("$rowid", rowId),
                            new SqliteParameter("$embedding", RecordMapper.EncodeVector(vectors[i])),
                        ],
                        token).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A vec0 KNN search. <paramref name="searchValue"/> may be a <see cref="string"/> (embedded
    /// through the query generator), <see cref="ReadOnlyMemory{T}"/> of <see cref="float"/>,
    /// <c>float[]</c> or <see cref="Embedding{T}"/> of <see cref="float"/>.
    /// <para>
    /// <b><see cref="MEVD.VectorSearchResult{TRecord}.Score"/> is the vec0 distance: lower is
    /// better</b> - the opposite polarity to <see cref="HybridSearchAsync{TInput}"/>'s fused score.
    /// <c>ScoreThreshold</c> is pushed down as <c>v.distance &lt;= $t</c>.
    /// </para>
    /// <para>
    /// <c>Skip</c> is honoured <b>client-side</b>, by discarding the first N rows while reading
    /// over <c>k = top + Skip</c>. <c>SQLITE_INDEX_CONSTRAINT_OFFSET</c> is skipped in every loop
    /// of <c>vec0BestIndex</c> and never given an <c>argvIndex</c>, so an <c>OFFSET</c> in the KNN
    /// query would be ignored rather than applied. It is never silently dropped.
    /// </para>
    /// </summary>
    /// <typeparam name="TInput">The search input type.</typeparam>
    /// <param name="searchValue">The query vector, or the text to embed into one.</param>
    /// <param name="top">How many results to return.</param>
    /// <param name="options">Filter, threshold, skip and vector-inclusion options.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The results, nearest first.</returns>
    public override async IAsyncEnumerable<MEVD.VectorSearchResult<TRecord>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        MEVD.VectorSearchOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var results = await SearchCoreAsync(searchValue, top, options, cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            yield return result;
        }
    }

    /// <summary>
    /// vec0 KNN fused with FTS5 bm25 by reciprocal rank fusion.
    /// <para>
    /// <b><see cref="MEVD.VectorSearchResult{TRecord}.Score"/> here is the RRF score: higher is
    /// better</b> - the opposite of <see cref="SearchAsync{TInput}"/>'s distance. Consistently,
    /// <c>HybridSearchOptions.ScoreThreshold</c> is applied to the fused score as a <b>client-side
    /// <c>&gt;=</c></b> after the query, where vector search pushes its threshold down as
    /// <c>distance &lt;=</c>.
    /// </para>
    /// <para>
    /// Pass <see cref="EdgeHybridSearchOptions{TRecord}"/> to tune <c>rrf_k</c> (default 60), the
    /// lane weights (default 1.0 each) and the per-lane candidate count (default
    /// <c>(top + Skip) * 4</c>, capped at 4096). An empty or all-whitespace keyword collection
    /// short-circuits the FTS lane and degenerates to plain KNN rather than emitting a malformed
    /// <c>MATCH</c>.
    /// </para>
    /// </summary>
    /// <typeparam name="TInput">The search input type.</typeparam>
    /// <param name="searchValue">The query vector, or the text to embed into one.</param>
    /// <param name="keywords">The keywords. Each is quoted, so FTS5 operators inside one are inert.</param>
    /// <param name="top">How many results to return.</param>
    /// <param name="options">Filter, threshold, skip, column filter and vector-inclusion options.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The results, best first.</returns>
    public async IAsyncEnumerable<MEVD.VectorSearchResult<TRecord>> HybridSearchAsync<TInput>(
        TInput searchValue,
        ICollection<string> keywords,
        int top,
        MEVD.HybridSearchOptions<TRecord>? options = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        var results = await HybridSearchCoreAsync(searchValue, keywords, top, options, cancellationToken)
            .ConfigureAwait(false);
        foreach (var result in results)
        {
            yield return result;
        }
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(MEVD.VectorStoreCollectionMetadata))
        {
            return new MEVD.VectorStoreCollectionMetadata
            {
                VectorStoreSystemName = EdgeVectorStoreException.SqliteSystemName,
                VectorStoreName = _database.Name,
                CollectionName = Name,
            };
        }

        if (serviceType == typeof(IEdgeDatabase))
        {
            return _database;
        }

        if (serviceType == typeof(CollectionModel))
        {
            return Model;
        }

        if (serviceType == typeof(EdgeVectorSchema))
        {
            return Schema;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    [RequiresDynamicCode(TrimMessage)]
    [RequiresUnreferencedCode(TrimMessage)]
    private static CollectionModel BuildReflectedModel(string name, EdgeVectorStoreCollectionOptions options) =>
        new EdgeCollectionModelBuilder(name).Build(
            typeof(TRecord),
            typeof(TKey),
            options.Definition,
            options.EmbeddingGenerator);

    private async Task AssertSqliteFloorAsync(CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var version = await connection.ScalarAsync<string>(
                "SELECT sqlite_version()",
                parameters: null,
                cancellationToken).ConfigureAwait(false) ?? "0.0.0";

            var parts = version.Split('.');
            var major = parts.Length > 0 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var m) ? m : 0;
            var minor = parts.Length > 1 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var n) ? n : 0;

            if (major < 3 || (major == 3 && minor < 39))
            {
                throw new EdgeVectorModelException(
                    EdgeErrorCode.SqliteVersionTooOld,
                    Name,
                    null,
                    $"SQLite {version} is below this provider's floor of 3.39. FULL OUTER JOIN needs 3.39, the " +
                    "rowid IN (...) push-down needs 3.38 and RETURNING needs 3.35. Qavren.Edge.Sqlite.Native pins " +
                    "3.53.4, so this only fires when Qavren.Edge.Sqlite is pointed at a system SQLite.");
            }
        }
    }

    private void AssertGeneratorDimensions()
    {
        var generator = Model.VectorProperty.EmbeddingGenerator ?? _options.EmbeddingGenerator;
        if (generator?.GetService(typeof(EmbeddingGeneratorMetadata)) is not EmbeddingGeneratorMetadata metadata)
        {
            // A third-party generator may publish no metadata at all - Semantic Kernel's own
            // adapter never propagates it - so the check defers to the first upsert, where the
            // actual vector length is compared instead.
            return;
        }

        if (metadata.DefaultModelDimensions is { } dimensions && dimensions != Model.VectorProperty.Dimensions)
        {
            throw new EdgeVectorModelException(
                EdgeErrorCode.VectorDimensionMismatch,
                Name,
                Model.VectorProperty.ModelName,
                $"The configured embedding generator produces {dimensions.ToString(CultureInfo.InvariantCulture)}-d " +
                $"vectors, and '{Model.VectorProperty.ModelName}' declares " +
                $"{Model.VectorProperty.Dimensions.ToString(CultureInfo.InvariantCulture)}. Change the preset or the " +
                "[VectorStoreVector(...)] width so the two agree.");
        }
    }

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> ResolveVectorsAsync(
        IReadOnlyList<TRecord> records,
        CancellationToken cancellationToken)
    {
        var property = Model.VectorProperty;
        var sources = records.Select(property.GetValueAsObject).ToArray();

        if (Model.EmbeddingGenerationRequired)
        {
            // One GenerateAsync call for the whole batch, before the transaction opens.
            var generated = await property.GenerateEmbeddingsAsync(sources!, cancellationToken).ConfigureAwait(false);
            var vectors = new ReadOnlyMemory<float>[generated.Count];
            for (var i = 0; i < generated.Count; i++)
            {
                if (generated[i] is not Embedding<float> embedding)
                {
                    throw new EdgeVectorStoreException(
                        EdgeErrorCode.VectorDimensionMismatch,
                        $"The configured embedding generator produced '{generated[i].GetType()}'. This provider " +
                        "stores float32 vectors only.")
                    {
                        VectorStoreName = _database.Name,
                        CollectionName = Name,
                        OperationName = EdgeVectorStoreOperations.Upsert,
                    };
                }

                vectors[i] = embedding.Vector;
            }

            AssertWidths(vectors);
            return vectors;
        }

        var precomputed = new ReadOnlyMemory<float>[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            if (!RecordMapper.TryGetVector(sources[i], out precomputed[i]))
            {
                throw MissingGenerator(property.ModelName);
            }
        }

        AssertWidths(precomputed);
        return precomputed;
    }

    private void AssertWidths(IReadOnlyList<ReadOnlyMemory<float>> vectors)
    {
        // The deferred half of the dimension check: a generator that publishes no metadata is
        // caught here, on the actual vector, rather than at collection create.
        foreach (var vector in vectors)
        {
            if (vector.Length != Model.VectorProperty.Dimensions)
            {
                throw new EdgeVectorStoreException(
                    EdgeErrorCode.VectorDimensionMismatch,
                    $"Vector property '{Model.VectorProperty.ModelName}' declares " +
                    $"{Model.VectorProperty.Dimensions.ToString(CultureInfo.InvariantCulture)} dimensions and the " +
                    $"value has {vector.Length.ToString(CultureInfo.InvariantCulture)}.")
                {
                    VectorStoreName = _database.Name,
                    CollectionName = Name,
                    OperationName = EdgeVectorStoreOperations.Upsert,
                };
            }
        }
    }

    private EdgeVectorStoreException MissingGenerator(string propertyName) =>
        new(
            EdgeErrorCode.EmbeddingGeneratorMissing,
            $"Vector property '{propertyName}' of collection '{Name}' holds a value that is not a float32 vector, " +
            "and no embedding generator is configured to turn it into one. Register one with AddOnnxEmbeddings(...) " +
            "before or after AddVectorStore(), or set EdgeVectorStoreOptions.EmbeddingGenerator.")
        {
            VectorStoreName = _database.Name,
            CollectionName = Name,
            OperationName = EdgeVectorStoreOperations.Upsert,
        };

    private async Task AssignKeyIfGeneratedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TRecord record,
        CancellationToken cancellationToken)
    {
        var key = Model.KeyProperty;
        if (!key.IsAutoGenerated)
        {
            return;
        }

        var current = key.GetValueAsObject(record);
        var type = Nullable.GetUnderlyingType(key.Type) ?? key.Type;

        if (type == typeof(Guid))
        {
            if (current is null || (Guid)current == Guid.Empty)
            {
                // Version 7 is time-ordered, so TEXT keys cluster instead of scattering the index.
                key.SetValueAsObject(record, Guid.CreateVersion7());
            }

            return;
        }

        if (type != typeof(int) && type != typeof(long))
        {
            return;
        }

        if (current is not null && Convert.ToInt64(current, CultureInfo.InvariantCulture) != 0)
        {
            return;
        }

        // Inside the write transaction, which SQLite serialises, so MAX + 1 cannot race.
        var next = await ScalarAsync<long>(
            connection,
            transaction,
            $"SELECT COALESCE(MAX(\"{Schema.KeyColumn}\"), 0) + 1 FROM \"{Schema.DataTable}\"",
            [],
            cancellationToken).ConfigureAwait(false);

        key.SetValueAsObject(record, type == typeof(int) ? (int)next : next);
    }

    private async Task<IReadOnlyList<TRecord>> ReadByKeysAsync(
        IReadOnlyList<TKey> keys,
        bool includeVectors,
        CancellationToken cancellationToken)
    {
        AssertVectorsReadable(includeVectors);

        var names = keys.Select((_, i) => "$k" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var sql = BuildSelectSql(includeVectors) +
                  $"\nWHERE d.\"{Schema.KeyColumn}\" IN ({string.Join(",", names)})";
        var parameters = keys.Select((k, i) => RecordMapper.Parameter(names[i], k)).ToArray();

        return await ReadRecordsAsync(sql, parameters, includeVectors, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TRecord>> ReadByFilterAsync(
        Expression<Func<TRecord, bool>> filter,
        int top,
        MEVD.FilteredRecordRetrievalOptions<TRecord>? options,
        CancellationToken cancellationToken)
    {
        var includeVectors = options?.IncludeVectors ?? false;
        AssertVectorsReadable(includeVectors);

        var translation = new EdgeFilterTranslator().Translate(filter, Model);
        var sql = new StringBuilder(BuildSelectSql(includeVectors));
        sql.Append("\nWHERE ").Append(translation.Sql);

        if (options?.OrderBy is { } orderBy)
        {
            var definition = orderBy(new MEVD.FilteredRecordRetrievalOptions<TRecord>.OrderByDefinition());
            if (definition.Values.Count > 0)
            {
                sql.Append("\nORDER BY ").Append(string.Join(
                    ", ",
                    definition.Values.Select(v =>
                        $"d.\"{Model.GetDataOrKeyProperty(v.PropertySelector).StorageName}\"" +
                        (v.Ascending ? string.Empty : " DESC"))));
            }
        }

        sql.Append("\nLIMIT $top OFFSET $skip");

        var parameters = new List<SqliteParameter>
        {
            new("$top", top),
            new("$skip", options?.Skip ?? 0),
        };
        parameters.AddRange(translation.Parameters.Select(p => RecordMapper.Parameter(p.Name, p.Value)));

        return await ReadRecordsAsync(sql.ToString(), parameters, includeVectors, cancellationToken)
            .ConfigureAwait(false);
    }

    private string BuildSelectSql(bool includeVectors)
    {
        var projection = string.Join(", ", _dataColumns.Select(c => $"d.\"{c}\""));
        var sql = new StringBuilder("SELECT ").Append(projection);

        if (includeVectors)
        {
            sql.Append(", v.\"").Append(Schema.VectorColumn).Append('"');
        }

        sql.Append("\nFROM \"").Append(Schema.DataTable).Append("\" d");

        if (includeVectors)
        {
            sql.Append("\nLEFT JOIN \"").Append(Schema.VectorTable).Append("\" v ON v.rowid = d.\"")
                .Append(Schema.RowIdColumn).Append('"');
        }

        return sql.ToString();
    }

    private void AssertVectorsReadable(bool includeVectors)
    {
        if (includeVectors && Model.EmbeddingGenerationRequired)
        {
            throw new NotSupportedException(
                $"Collection '{Name}' generates its embeddings from a source property, so there is no vector " +
                "property to read them back into. IncludeVectors is not supported on a model with embedding " +
                "generation.");
        }
    }

    private async Task<IReadOnlyList<TRecord>> ReadRecordsAsync(
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        bool includeVectors,
        CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                return await connection.QueryAsync(
                    sql,
                    reader => MaterialiseRecord(reader, 0, includeVectors ? _dataColumns.Length : -1),
                    parameters,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                throw Wrap(EdgeVectorStoreOperations.Get, ex);
            }
        }
    }

    private TRecord MaterialiseRecord(SqliteDataReader reader, int firstDataOrdinal, int vectorOrdinal)
    {
        var record = Model.CreateRecord<TRecord>()!;

        for (var i = 0; i < _dataProperties.Length; i++)
        {
            var property = _dataProperties[i];
            property.SetValueAsObject(record, RecordMapper.ReadValue(reader, firstDataOrdinal + i, property.Type));
        }

        if (vectorOrdinal >= 0 && !reader.IsDBNull(vectorOrdinal))
        {
            RecordMapper.SetVector(
                Model.VectorProperty,
                record,
                RecordMapper.DecodeVector(reader.GetFieldValue<byte[]>(vectorOrdinal)));
        }

        return record;
    }

    private async Task<IReadOnlyList<MEVD.VectorSearchResult<TRecord>>> SearchCoreAsync<TInput>(
        TInput searchValue,
        int top,
        MEVD.VectorSearchOptions<TRecord>? options,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);

        var skip = options?.Skip ?? 0;
        ArgumentOutOfRangeException.ThrowIfNegative(skip);

        var k = (long)top + skip;
        if (k > RrfDefaults.MaxCandidateCount)
        {
            throw new EdgeVectorStoreException(
                EdgeErrorCode.KnnLimitExceeded,
                $"top + Skip is {k.ToString(CultureInfo.InvariantCulture)}, above vec0's SQLITE_VEC_VEC0_K_MAX of " +
                $"{RrfDefaults.MaxCandidateCount.ToString(CultureInfo.InvariantCulture)}. Narrow the query or page " +
                "with a filter instead.")
            {
                VectorStoreName = _database.Name,
                CollectionName = Name,
                OperationName = EdgeVectorStoreOperations.VectorSearch,
            };
        }

        var includeVectors = options?.IncludeVectors ?? false;
        AssertVectorsReadable(includeVectors);

        var query = await ResolveSearchVectorAsync(searchValue, cancellationToken).ConfigureAwait(false);
        var translation = options?.Filter is { } filter ? new EdgeFilterTranslator().Translate(filter, Model) : null;

        var sql = Schema.BuildKnnSql(translation is not null, options?.ScoreThreshold is not null, includeVectors);
        if (translation is not null)
        {
            sql = sql.Replace(EdgeVectorSchema.FilterPlaceholder, translation.Sql, StringComparison.Ordinal);
        }

        var parameters = new List<SqliteParameter>
        {
            new("$query", RecordMapper.EncodeVector(query)),
            new("$k", (int)k),
        };

        if (options?.ScoreThreshold is { } threshold)
        {
            parameters.Add(new SqliteParameter("$scoreThreshold", threshold));
        }

        if (translation is not null)
        {
            parameters.AddRange(translation.Parameters.Select(p => RecordMapper.Parameter(p.Name, p.Value)));
        }

        var distanceOrdinal = 1 + _dataColumns.Length;
        var vectorOrdinal = includeVectors ? distanceOrdinal + 1 : -1;

        var rows = await ExecuteSearchAsync(
            sql,
            parameters,
            EdgeVectorStoreOperations.VectorSearch,
            reader => new MEVD.VectorSearchResult<TRecord>(
                MaterialiseRecord(reader, 1, vectorOrdinal),
                reader.GetDouble(distanceOrdinal)),
            cancellationToken).ConfigureAwait(false);

        return skip == 0 ? rows : [.. rows.Skip(skip)];
    }

    private async Task<IReadOnlyList<MEVD.VectorSearchResult<TRecord>>> HybridSearchCoreAsync<TInput>(
        TInput searchValue,
        ICollection<string> keywords,
        int top,
        MEVD.HybridSearchOptions<TRecord>? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);

        var skip = options?.Skip ?? 0;
        var edgeOptions = options as EdgeHybridSearchOptions<TRecord>;
        var rrfK = edgeOptions?.RrfK ?? Rrf.K;
        var weightVector = edgeOptions?.VectorWeight ?? Rrf.VectorWeight;
        var weightKeyword = edgeOptions?.KeywordWeight ?? Rrf.KeywordWeight;
        var candidates = Math.Min(
            edgeOptions?.CandidateCount ?? Rrf.ResolveCandidateCount(top, skip),
            RrfDefaults.MaxCandidateCount);

        var live = keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (live.Length == 0)
        {
            // Degenerates to plain KNN rather than emitting a malformed MATCH. The score stays an
            // RRF score - higher is better - so the polarity of this member never depends on which
            // keywords a caller happened to pass.
            return await DegenerateToKnnAsync(
                searchValue, top, skip, rrfK, weightVector, options, cancellationToken).ConfigureAwait(false);
        }

        var includeVectors = options?.IncludeVectors ?? false;
        AssertVectorsReadable(includeVectors);

        var query = await ResolveSearchVectorAsync(searchValue, cancellationToken).ConfigureAwait(false);
        var translation = options?.Filter is { } filter ? new EdgeFilterTranslator().Translate(filter, Model) : null;

        var sql = Schema.BuildHybridRrfSql(translation is not null, includeVectors);
        if (translation is not null)
        {
            sql = sql.Replace(EdgeVectorSchema.FilterPlaceholder, translation.Sql, StringComparison.Ordinal);
        }

        // With one full-text column the unqualified expression already means that column, so the
        // column filter is emitted only when the caller asked for a specific property.
        var columnFilter = options?.AdditionalProperty is null
            ? null
            : Model.GetFullTextDataPropertyOrSingle(options.AdditionalProperty).StorageName;

        var parameters = new List<SqliteParameter>
        {
            new("$query", RecordMapper.EncodeVector(query)),
            new("$cand", candidates),
            new("$rrfK", rrfK),
            new("$wVector", weightVector),
            new("$wKeyword", weightKeyword),
            new("$keywords", EdgeVectorSchema.BuildMatchExpression(
                live,
                edgeOptions?.KeywordCombinator ?? KeywordCombinator,
                columnFilter)),
            new("$top", top),
            new("$skip", skip),
        };

        if (translation is not null)
        {
            parameters.AddRange(translation.Parameters.Select(p => RecordMapper.Parameter(p.Name, p.Value)));
        }

        var scoreOrdinal = 1 + _dataColumns.Length;
        var vectorOrdinal = includeVectors ? scoreOrdinal + 3 : -1;

        var rows = await ExecuteSearchAsync(
            sql,
            parameters,
            EdgeVectorStoreOperations.HybridSearch,
            reader => new MEVD.VectorSearchResult<TRecord>(
                MaterialiseRecord(reader, 1, vectorOrdinal),
                reader.GetDouble(scoreOrdinal)),
            cancellationToken).ConfigureAwait(false);

        var results = ApplyHybridThreshold(rows, options?.ScoreThreshold);

        if (Logger is { } logger)
        {
            LogHybridSearchExecuted(logger, Name, candidates, results.Count, null);
        }

        return results;
    }

    private async Task<IReadOnlyList<MEVD.VectorSearchResult<TRecord>>> DegenerateToKnnAsync<TInput>(
        TInput searchValue,
        int top,
        int skip,
        int rrfK,
        double weightVector,
        MEVD.HybridSearchOptions<TRecord>? options,
        CancellationToken cancellationToken)
    {
        var vectorOptions = new MEVD.VectorSearchOptions<TRecord>
        {
            Filter = options?.Filter,
            IncludeVectors = options?.IncludeVectors ?? false,
            Skip = skip,
        };

        var knn = await SearchCoreAsync(searchValue, top, vectorOptions, cancellationToken).ConfigureAwait(false);

        // The fused score the full query would have produced with an empty FTS lane: the keyword
        // half of the sum, COALESCE(1.0 / ($rrfK + fts.rank), 0.0) * $wKeyword, is zero everywhere.
        var fused = knn
            .Select((r, i) => new MEVD.VectorSearchResult<TRecord>(r.Record, weightVector / (rrfK + skip + i + 1)))
            .ToArray();

        return ApplyHybridThreshold(fused, options?.ScoreThreshold);
    }

    private static IReadOnlyList<MEVD.VectorSearchResult<TRecord>> ApplyHybridThreshold(
        IReadOnlyList<MEVD.VectorSearchResult<TRecord>> rows,
        double? scoreThreshold) =>
        scoreThreshold is { } threshold
            ? [.. rows.Where(r => r.Score is { } score && score >= threshold)]
            : rows;

    private async Task<IReadOnlyList<T>> ExecuteSearchAsync<T>(
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        string operation,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                return await connection.QueryAsync(sql, map, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                throw Wrap(operation, ex);
            }
        }
    }

    private async Task<ReadOnlyMemory<float>> ResolveSearchVectorAsync(
        object? searchValue,
        CancellationToken cancellationToken)
    {
        if (RecordMapper.TryGetVector(searchValue, out var vector))
        {
            return vector;
        }

        if (searchValue is not string text)
        {
            throw new NotSupportedException(
                $"This provider cannot search with a '{searchValue?.GetType().ToString() ?? "null"}'. Pass a string, " +
                "ReadOnlyMemory<float>, float[] or Embedding<float>.");
        }

        // The query generator, in order: the per-collection override, the sibling the builder
        // resolved onto the model, then the document generator itself.
        var generator = _options.QueryEmbeddingGenerator
            ?? Model.VectorProperty.EmbeddingGenerator
            ?? _options.EmbeddingGenerator;

        if (generator is not IEmbeddingGenerator<string, Embedding<float>> typed)
        {
            throw MissingGenerator(Model.VectorProperty.ModelName);
        }

        var embeddings = await typed.GenerateAsync([text], options: null, cancellationToken).ConfigureAwait(false);
        return embeddings[0].Vector;
    }

    private async Task RunStatementsAsync(
        IReadOnlyList<string> statements,
        string operation,
        CancellationToken cancellationToken) =>
        _ = await ExecuteInTransactionAsync(
            operation,
            async (connection, transaction, token) =>
            {
                foreach (var statement in statements)
                {
                    await ExecuteAsync(connection, transaction, statement, [], token).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);

    private async Task<T> ExecuteInTransactionAsync<T>(
        string operation,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _database.ExecuteInTransactionAsync(work, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw Wrap(operation, ex);
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        CancellationToken cancellationToken)
    {
        var command = Command(connection, transaction, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        CancellationToken cancellationToken)
        where T : struct
    {
        var command = Command(connection, transaction, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull
                ? default
                : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<SqliteParameter> parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private EdgeVectorStoreException Wrap(string operation, SqliteException inner) =>
        new(
            EdgeErrorCode.VectorStoreOperationFailed,
            $"SQLite failed during '{operation}' on collection '{Name}': {inner.Message}",
            inner)
        {
            VectorStoreName = _database.Name,
            CollectionName = Name,
            OperationName = operation,
        };
}
