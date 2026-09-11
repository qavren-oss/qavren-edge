using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.VectorData.Internal;

/// <summary>
/// One collection as the builder registered it. This is what the diagnostics contributor reports
/// and what the lifecycle observer walks looking for FTS sidecars to merge; a collection reached
/// only through <c>GetCollection</c> at runtime has no registration and appears in neither, which
/// is why <c>AddVectorCollectionMigration</c> is the recommended path.
/// </summary>
/// <param name="DatabaseName">The sub-project 1 database key.</param>
/// <param name="CollectionName">The collection name.</param>
/// <param name="DataTable">The data table.</param>
/// <param name="VectorTable">The vec0 sidecar.</param>
/// <param name="FullTextTable">The FTS5 sidecar, or null.</param>
/// <param name="Dimensions">The declared vector width.</param>
/// <param name="DistanceFunction">The MEVD distance function.</param>
/// <param name="ChunkSize">The vec0 chunk size.</param>
/// <param name="FullTextTokenizer">The FTS5 tokenizer.</param>
/// <param name="KeyType">The key CLR type name.</param>
/// <param name="Generator">The configured generator's type name, or null.</param>
/// <param name="GeneratorDimensions">The generator's published width, or null.</param>
/// <param name="RrfK">The reciprocal-rank-fusion smoothing constant.</param>
/// <param name="VectorWeight">The vector lane's weight.</param>
/// <param name="KeywordWeight">The keyword lane's weight.</param>
internal sealed record EdgeVectorCollectionRegistration(
    string DatabaseName,
    string CollectionName,
    string DataTable,
    string VectorTable,
    string? FullTextTable,
    int Dimensions,
    string DistanceFunction,
    int ChunkSize,
    string FullTextTokenizer,
    string KeyType,
    string? Generator,
    int? GeneratorDimensions,
    int RrfK,
    double VectorWeight,
    double KeywordWeight)
{
    /// <summary>Set by the store factory from <c>IncludeRowCountsInDiagnostics</c>.</summary>
    public bool IncludeRowCounts { get; set; }
}

/// <summary>Build-time bookkeeping, resolved as a singleton by the observer and the contributor.</summary>
internal sealed class EdgeVectorCollectionRegistry
{
    private readonly List<EdgeVectorCollectionRegistration> _registrations = [];

    /// <summary>Every collection registered so far.</summary>
    public IReadOnlyList<EdgeVectorCollectionRegistration> Registrations => _registrations;

    /// <summary>Counting rows is one query per collection, so it is off unless the store asks.</summary>
    public bool IncludeRowCounts
    {
        get;
        set
        {
            field = value;
            foreach (var registration in _registrations)
            {
                registration.IncludeRowCounts = value;
            }
        }
    }

    /// <summary>Records one collection.</summary>
    /// <param name="registration">The collection.</param>
    public void Add(EdgeVectorCollectionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        registration.IncludeRowCounts = IncludeRowCounts;
        _registrations.Add(registration);
    }
}

/// <summary>
/// A generator that is never invoked and exists only so a model can be BUILT.
/// <para>
/// <c>AddVectorCollectionMigration&lt;TKey, TRecord&gt;</c> runs at builder time, where the
/// container does not exist yet, so the application's real <see cref="IEmbeddingGenerator"/> is
/// out of reach. MEVD's model builder refuses a <see cref="string"/> source vector property unless
/// some generator can turn it into an <see cref="Embedding{T}"/> - and the DDL it is about to emit
/// needs only the DECLARED width, the distance function and the column names, none of which a
/// generator contributes. So a stand-in is passed, the model is used for
/// <see cref="EdgeVectorSchema.BuildCreateSql()"/> and then discarded; the real generator is
/// resolved at store-resolve time and is the one every read and write actually uses.
/// </para>
/// <para>
/// It publishes no <see cref="EmbeddingGeneratorMetadata"/>, so nothing can mistake it for a
/// configured generator and read a width off it, and <see cref="GenerateAsync"/> throws.
/// </para>
/// </summary>
internal sealed class SchemaOnlyEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The single instance; it holds no state.</summary>
    public static SchemaOnlyEmbeddingGenerator Instance { get; } = new();

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "SchemaOnlyEmbeddingGenerator exists to let a collection model be built for DDL emission and must " +
            "never be asked for an embedding. Register a real generator with AddOnnxEmbeddings(...).");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// Turns a collection's options into the <see cref="EdgeVectorSchema"/> that emits its DDL. The
/// collection constructor, <c>AddVectorCollectionMigration</c> and <c>AddVectorCollection</c> all
/// come through here, which is what makes <see cref="EdgeVectorSchema.BuildCreateSql()"/> the
/// single source spec 12.4 says it is.
/// </summary>
internal static class EdgeVectorCollectionSchemaFactory
{
    /// <summary>Builds the schema for one collection.</summary>
    /// <param name="model">The built collection model.</param>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="options">The per-collection overrides.</param>
    /// <returns>The schema.</returns>
    public static EdgeVectorSchema Create(
        CollectionModel model,
        string collectionName,
        EdgeVectorStoreCollectionOptions options) =>
        new(model, collectionName, ToStoreOptions(options), options.AlwaysCreateFullTextIndex);

    /// <summary>Projects the per-collection overrides onto a store-options instance.</summary>
    /// <param name="options">The per-collection overrides.</param>
    /// <returns>Store options carrying every override that was set.</returns>
    public static EdgeVectorStoreOptions ToStoreOptions(EdgeVectorStoreCollectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var store = new EdgeVectorStoreOptions();

        if (options.VectorTableName is { Length: > 0 } vectorTable)
        {
            // A literal, not a format: a brace in a user-chosen table name would otherwise be a
            // formatting accident, so the override is escaped rather than passed through as-is.
            store.VectorTableNameFormat = Escape(vectorTable);
        }

        if (options.FullTextTableName is { Length: > 0 } fullTextTable)
        {
            store.FullTextTableNameFormat = Escape(fullTextTable);
        }

        if (options.ChunkSize is { } chunkSize)
        {
            store.ChunkSize = chunkSize;
        }

        if (options.FullTextTokenizer is { } tokenizer)
        {
            store.FullTextTokenizer = tokenizer;
        }

        if (options.RemoveDiacritics is { } removeDiacritics)
        {
            store.FullTextRemoveDiacritics = removeDiacritics;
        }

        return store;

        static string Escape(string literal) => literal
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
    }
}

/// <summary>
/// A collection's DDL as a sub-project 1 <see cref="IEdgeMigration"/>, so the schema is versioned
/// by the existing migrator at startup order 100 with <c>PRAGMA user_version</c> bookkeeping and
/// sub-project 1's failure semantics, instead of appearing on first use.
/// <para>
/// The statements come from <see cref="EdgeVectorSchema.BuildCreateSql()"/> - the single source
/// shared with <c>AddVectorCollection</c> and bare <c>EnsureCollectionExistsAsync</c> - so the
/// three paths cannot drift.
/// </para>
/// </summary>
internal sealed class EdgeVectorCollectionMigration : IEdgeMigration
{
    private readonly IReadOnlyList<string> _statements;

    /// <summary>Creates the migration.</summary>
    /// <param name="version">The migration version, unique and ascending across the database.</param>
    /// <param name="collectionName">Named in the migration name, so a failure says which collection.</param>
    /// <param name="statements">The create statements, in execution order.</param>
    public EdgeVectorCollectionMigration(int version, string collectionName, IReadOnlyList<string> statements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(statements);

        Version = version;
        Name = "CreateVectorCollection:" + collectionName;
        _statements = statements;
    }

    /// <inheritdoc />
    public int Version { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var statement in _statements)
        {
            // The migrator owns the transaction, so nothing here opens one of its own.
            await connection.ExecuteAsync(statement, parameters: null, cancellationToken).ConfigureAwait(false);
        }
    }
}
