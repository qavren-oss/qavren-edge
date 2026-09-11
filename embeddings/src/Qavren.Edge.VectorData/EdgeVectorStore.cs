using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Sqlite;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData;

/// <summary>
/// A <c>Microsoft.Extensions.VectorData</c> store over one sub-project 1
/// <see cref="IEdgeDatabase"/>. It never constructs a connection of its own, which is what lets it
/// ride on an encrypted, pooled, pragma-configured database that a connection-string-based
/// connector structurally cannot reach.
/// </summary>
public sealed class EdgeVectorStore : MEVD.VectorStore
{
    private const string TrimMessage =
        "Reflects over TRecord. Use GetDynamicCollection in trimmed or AOT apps.";

    private static readonly string[] Fts5ShadowSuffixes =
        ["_data", "_idx", "_content", "_docsize", "_config"];

    private static readonly string[] Vec0ShadowChildren =
        ["info", "chunks", "rowids", "auxiliary"];

    private static readonly string[] Vec0NumberedShadowChildren =
        ["vector_chunks", "metadatachunks", "metadatatext"];

    private readonly IEdgeDatabase _database;
    private readonly EdgeVectorStoreOptions _options;
    private readonly IEmbeddingGenerator? _embeddingGenerator;
    private readonly IEmbeddingGenerator? _queryEmbeddingGenerator;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Creates the store.</summary>
    /// <param name="database">The sub-project 1 database every collection lives in.</param>
    /// <param name="options">Store-wide defaults. Null takes every default.</param>
    /// <param name="embeddingGenerator">
    /// The DI seam. <c>AddVectorStore</c> passes the container's registered
    /// <see cref="IEmbeddingGenerator"/> here, and it is used <b>only</b> when
    /// <see cref="EdgeVectorStoreOptions.EmbeddingGenerator"/> is still null - an explicitly-set
    /// option always wins. Without this parameter the flagship four-call path would register a
    /// generator the store could never see, and a <see cref="string"/> source property would fail
    /// with <c>EmbeddingGeneratorMissing</c> on the first upsert.
    /// </param>
    /// <param name="queryEmbeddingGenerator">
    /// The generator that embeds a search value, applied only when
    /// <see cref="EdgeVectorStoreOptions.QueryEmbeddingGenerator"/> is still null.
    /// </param>
    /// <param name="loggerFactory">Receives collection and hybrid-search events (800-802).</param>
    public EdgeVectorStore(
        IEdgeDatabase database,
        EdgeVectorStoreOptions? options = null,
        IEmbeddingGenerator? embeddingGenerator = null,
        IEmbeddingGenerator? queryEmbeddingGenerator = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _options = options ?? new EdgeVectorStoreOptions();
        _embeddingGenerator = _options.EmbeddingGenerator ?? embeddingGenerator;
        _queryEmbeddingGenerator = _options.QueryEmbeddingGenerator ?? queryEmbeddingGenerator;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    [RequiresDynamicCode(TrimMessage)]
    [RequiresUnreferencedCode(TrimMessage)]
    public override EdgeVectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name,
        MEVD.VectorStoreCollectionDefinition? definition = null)
    {
        var options = CollectionOptions(name, definition);
        var collection = new EdgeVectorStoreCollection<TKey, TRecord>(_database, name, options);
        collection.Logger = _loggerFactory?.CreateLogger<EdgeVectorStoreCollection<TKey, TRecord>>();
        return collection;
    }

    /// <inheritdoc />
    public override EdgeDynamicVectorStoreCollection GetDynamicCollection(
        string name,
        MEVD.VectorStoreCollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var collection = new EdgeDynamicVectorStoreCollection(_database, name, CollectionOptions(name, definition));
        collection.Logger = _loggerFactory?.CreateLogger<EdgeDynamicVectorStoreCollection>();
        return collection;
    }

    /// <summary>
    /// Data tables only, and <b>the exclusion is structural, not by name</b>. A virtual table is
    /// recognised from its own DDL in <c>sqlite_master.sql</c> (<c>USING vec0</c> /
    /// <c>USING fts5</c>), and a shadow table by carrying one of the module's <b>known</b> child
    /// names under a parent that is itself a virtual table of that module - FTS5's five
    /// (<c>_data</c>, <c>_idx</c>, <c>_content</c>, <c>_docsize</c>, <c>_config</c>) and vec0's
    /// (<c>_info</c>, <c>_chunks</c>, <c>_rowids</c>, <c>_auxiliary</c> and the numbered
    /// <c>_vector_chunksNN</c> / <c>_metadatachunksNN</c> / <c>_metadatatextNN</c>).
    /// <c>sqlite_%</c> is excluded as well. A table called <c>notes_vec_archive</c> next to a vec0
    /// table called <c>notes_vec</c> is a data table and is listed.
    /// <para>
    /// Name-prefix filtering would be wrong in <b>both</b> directions, because
    /// <see cref="EdgeVectorStoreOptions.VectorTableNameFormat"/> and
    /// <see cref="EdgeVectorStoreCollectionOptions.VectorTableName"/> let a sidecar be called
    /// anything, and a consumer's own data table may legitimately be called <c>foo_vec</c>.
    /// Upstream's connector leaks its vec0 tables here; this one does not.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The data-table names, in <c>sqlite_master</c> order.</returns>
    public override async IAsyncEnumerable<string> ListCollectionNamesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rows = await ReadMasterAsync(cancellationToken).ConfigureAwait(false);

        var fts5 = rows.Where(r => IsVirtual(r.Sql, "fts5")).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var vec0 = rows.Where(r => IsVirtual(r.Sql, "vec0")).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.Name.StartsWith("sqlite_", StringComparison.Ordinal)
                || fts5.Contains(row.Name)
                || vec0.Contains(row.Name)
                || IsFts5Shadow(row.Name, fts5)
                || IsVec0Shadow(row.Name, vec0))
            {
                continue;
            }

            yield return row.Name;
        }
    }

    /// <inheritdoc />
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var rows = await ReadMasterAsync(cancellationToken).ConfigureAwait(false);
        return rows.Any(r =>
            string.Equals(r.Name, name, StringComparison.Ordinal)
            && !IsVirtual(r.Sql, "fts5")
            && !IsVirtual(r.Sql, "vec0"));
    }

    /// <summary>
    /// Drops a collection's three tables and its triggers without needing a record type: the
    /// sidecar names come from the store's name formats, which is the same source
    /// <see cref="EdgeVectorSchema"/> uses.
    /// </summary>
    /// <param name="name">The collection name.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the transaction has committed.</returns>
    public override async Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var vectorTable = string.Format(CultureInfo.InvariantCulture, _options.VectorTableNameFormat, name);
        var fullTextTable = string.Format(CultureInfo.InvariantCulture, _options.FullTextTableNameFormat, name);

        string[] statements =
        [
            $"DROP TRIGGER IF EXISTS \"{vectorTable}_ad\"",
            $"DROP TRIGGER IF EXISTS \"{fullTextTable}_ad\"",
            $"DROP TRIGGER IF EXISTS \"{fullTextTable}_au\"",
            $"DROP TRIGGER IF EXISTS \"{fullTextTable}_ai\"",
            $"DROP TABLE IF EXISTS \"{fullTextTable}\"",
            $"DROP TABLE IF EXISTS \"{vectorTable}\"",
            $"DROP TABLE IF EXISTS \"{name}\"",
        ];

        try
        {
            await _database.ExecuteInTransactionAsync(
                async (connection, transaction, token) =>
                {
                    foreach (var statement in statements)
                    {
                        var command = connection.CreateCommand();
                        await using (command.ConfigureAwait(false))
                        {
                            command.Transaction = transaction;
                            command.CommandText = statement;
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw new EdgeVectorStoreException(
                EdgeErrorCode.VectorStoreOperationFailed,
                $"SQLite failed dropping collection '{name}': {ex.Message}",
                ex)
            {
                VectorStoreName = _database.Name,
                CollectionName = name,
                OperationName = EdgeVectorStoreOperations.DeleteCollection,
            };
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

        if (serviceType == typeof(MEVD.VectorStoreMetadata))
        {
            return new MEVD.VectorStoreMetadata
            {
                VectorStoreSystemName = EdgeVectorStoreException.SqliteSystemName,
                VectorStoreName = _database.Name,
            };
        }

        if (serviceType == typeof(IEdgeDatabase))
        {
            return _database;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    private static bool IsVirtual(string? sql, string module) =>
        sql is not null
        && sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("USING " + module, StringComparison.OrdinalIgnoreCase);

    private static bool IsFts5Shadow(string name, HashSet<string> fts5) =>
        Fts5ShadowSuffixes.Any(suffix =>
            name.EndsWith(suffix, StringComparison.Ordinal)
            && fts5.Contains(name[..^suffix.Length]));

    // The same shape as the FTS5 rule spec 8 states: a KNOWN child name of a table that is itself
    // a vec0 virtual table. "Any child of a vec0 table" was the earlier rule and it was too broad -
    // with a vec0 table "notes_vec" present it silently hid a consumer's own data table called
    // "notes_vec_archive", which is exactly the false negative spec 8 warns about. sqlite-vec
    // 0.1.9 creates "_info", "_chunks", "_rowids", "_auxiliary" and the two-digit-numbered
    // "_vector_chunksNN" / "_metadatachunksNN" / "_metadatatextNN" families; nothing else is
    // hidden, and ListCollectionNamesTests asserts both directions.
    private static bool IsVec0Shadow(string name, HashSet<string> vec0) =>
        vec0.Any(parent =>
            name.Length > parent.Length + 1
            && name.StartsWith(parent, StringComparison.Ordinal)
            && name[parent.Length] == '_'
            && IsVec0ShadowChild(name.AsSpan(parent.Length + 1)));

    private static bool IsVec0ShadowChild(ReadOnlySpan<char> child)
    {
        foreach (var known in Vec0ShadowChildren)
        {
            if (child.Equals(known, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var prefix in Vec0NumberedShadowChildren)
        {
            if (child.Length > prefix.Length
                && child.StartsWith(prefix, StringComparison.Ordinal)
                && IsAllDigits(child[prefix.Length..]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private async Task<IReadOnlyList<MasterRow>> ReadMasterAsync(CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                return await connection.QueryAsync(
                    "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name",
                    reader => new MasterRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)),
                    parameters: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                throw new EdgeVectorStoreException(
                    EdgeErrorCode.VectorStoreOperationFailed,
                    $"SQLite failed reading sqlite_master: {ex.Message}",
                    ex)
                {
                    VectorStoreName = _database.Name,
                    OperationName = EdgeVectorStoreOperations.ListCollectionNames,
                };
            }
        }
    }

    private EdgeVectorStoreCollectionOptions CollectionOptions(
        string name,
        MEVD.VectorStoreCollectionDefinition? definition) =>
        new()
        {
            Definition = definition,
            EmbeddingGenerator = definition?.EmbeddingGenerator ?? _embeddingGenerator,
            QueryEmbeddingGenerator = _queryEmbeddingGenerator,

            // The store's name FORMATS are resolved here, so the collection sees literal table
            // names and the two never disagree about which sidecar a collection owns.
            VectorTableName = string.Format(CultureInfo.InvariantCulture, _options.VectorTableNameFormat, name),
            FullTextTableName = string.Format(CultureInfo.InvariantCulture, _options.FullTextTableNameFormat, name),
            ChunkSize = _options.ChunkSize,
            FullTextTokenizer = _options.FullTextTokenizer,
            RemoveDiacritics = _options.FullTextRemoveDiacritics,
            KeywordCombinator = _options.KeywordCombinator,
            Rrf = _options.Rrf,
        };

    private sealed record MasterRow(string Name, string? Sql);
}
