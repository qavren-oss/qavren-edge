using System.Globalization;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>One row of <c>&lt;prefix&gt;_document</c>.</summary>
/// <param name="SourceId">The source.</param>
/// <param name="DocumentId">The document.</param>
/// <param name="ContentHash">The source bytes' hash, or <see cref="Ingestion.ContentHash.Zero"/>.</param>
/// <param name="RecipeHash">The recipe the chunks were produced under.</param>
/// <param name="SizeBytes">The declared size at the time, or null.</param>
/// <param name="ModifiedUtc">The declared mtime at the time, or null.</param>
/// <param name="ExtractorId">The extractor that produced it.</param>
/// <param name="ChunkCount">Chunks currently stored for it.</param>
/// <param name="Status">One of <see cref="IngestionDocumentStatus"/>.</param>
/// <param name="Error">The last failure message, when the status is <c>Failed</c>.</param>
/// <param name="LastRunId">The run that last touched it. Drives pruning.</param>
/// <param name="UpdatedUtc">When the row was written.</param>
internal sealed record IngestionDocumentState(
    string SourceId,
    string DocumentId,
    ContentHash ContentHash,
    ContentHash RecipeHash,
    long? SizeBytes,
    DateTimeOffset? ModifiedUtc,
    string? ExtractorId,
    int ChunkCount,
    string Status,
    string? Error,
    string? LastRunId,
    DateTimeOffset UpdatedUtc);

/// <summary>The four populations spec 12's diagnostics keys report.</summary>
/// <param name="Documents">All state rows.</param>
/// <param name="Indexed">Rows whose status is <c>Indexed</c>.</param>
/// <param name="Failed">Rows whose status is <c>Failed</c>.</param>
/// <param name="NoTextLayer">Rows whose status is <c>NoTextLayer</c>.</param>
/// <param name="Stale">Rows not stamped with the newest completed run's id.</param>
/// <param name="RecipeStale">Rows whose stored recipe hash differs from the current one.</param>
internal sealed record IngestionStateCounts(
    int Documents, int Indexed, int Failed, int NoTextLayer, int Stale, int RecipeStale);

/// <summary>The newest <c>&lt;prefix&gt;_run</c> row.</summary>
/// <param name="RunId">Its id.</param>
/// <param name="Outcome">Its outcome, parsed.</param>
/// <param name="SuspendReason">Its suspend reason.</param>
/// <param name="StartedUtc">When it started.</param>
internal sealed record IngestionRunSummary(
    string RunId, IngestionRunOutcome? Outcome, string? SuspendReason, DateTimeOffset StartedUtc);

/// <summary>
/// Plain SQL through <see cref="IEdgeDatabase"/> over the three tables
/// <see cref="IngestionSchema.BuildStateSql"/> created. It opens its own connections and its own
/// transactions and never calls an SP2 collection method — the transaction invariant of spec 9.5
/// is a property of who calls what, and this type is one half of it.
/// </summary>
internal sealed class IngestionStateStore
{
    private readonly IEdgeDatabase _database;
    private readonly string _prefix;

    public IngestionStateStore(IEdgeDatabase database, string tablePrefix)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePrefix);

        _database = database;
        _prefix = tablePrefix;
        DocumentTable = tablePrefix + "_document";
        RunTable = tablePrefix + "_run";
        MetaTable = tablePrefix + "_meta";
    }

    public string DocumentTable { get; }

    public string RunTable { get; }

    public string MetaTable { get; }

    /// <summary>
    /// Asserts the tables exist, the schema version is this build's and the hash algorithm matches.
    /// Raised here rather than mis-diffing later.
    /// </summary>
    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var present = await connection.ScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ($d,$r,$m)",
                [
                    new SqliteParameter("$d", DocumentTable),
                    new SqliteParameter("$r", RunTable),
                    new SqliteParameter("$m", MetaTable),
                ],
                cancellationToken).ConfigureAwait(false);

            if (present != 3)
            {
                throw new EdgeIngestionStateException(
                    EdgeErrorCode.IngestionStateMissing,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The ingestion state tables '{0}', '{1}' and '{2}' are not all present in database '{3}'.",
                        DocumentTable,
                        RunTable,
                        MetaTable,
                        _database.Name))
                {
                    Remediation =
                        "Call AddIngestion(version) on the builder so its migration runs, and make sure the host " +
                        "has started before the first RunAsync.",
                };
            }

            var storedVersion = await MetaAsync(
                connection, IngestionSchema.MetaSchemaVersionKey, cancellationToken).ConfigureAwait(false);
            if (!int.TryParse(storedVersion, CultureInfo.InvariantCulture, out var version)
                || version != IngestionSchema.StateSchemaVersion)
            {
                throw new EdgeIngestionStateException(
                    EdgeErrorCode.IngestionStateSchemaUnsupported,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The ingestion state schema on disk reports version '{0}'; this build writes version {1}.",
                        storedVersion ?? "(absent)",
                        IngestionSchema.StateSchemaVersion))
                {
                    ExpectedSchemaVersion = IngestionSchema.StateSchemaVersion,
                    ActualSchemaVersion = int.TryParse(storedVersion, CultureInfo.InvariantCulture, out var actual)
                        ? actual
                        : null,
                    Remediation =
                        "This database was written by a newer Qavren.Edge.Ingestion. Upgrade the package, or drop " +
                        "the state tables and re-ingest.",
                };
            }

            var algorithm = await MetaAsync(
                connection, IngestionSchema.MetaHashAlgorithmKey, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(algorithm, ContentHash.AlgorithmId, StringComparison.Ordinal))
            {
                throw new EdgeIngestionStateException(
                    EdgeErrorCode.IngestionHashAlgorithmMismatch,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The ingestion state was written with hash algorithm '{0}'; this build uses '{1}'.",
                        algorithm ?? "(absent)",
                        ContentHash.AlgorithmId))
                {
                    Remediation =
                        "Every stored content hash was produced by a different algorithm, so the incremental diff " +
                        "would be meaningless. Delete the state rows and the collection's chunks, then re-ingest.",
                };
            }
        }
    }

    /// <summary>Reads one document row, or null.</summary>
    public async Task<IngestionDocumentState?> GetDocumentAsync(
        string collection, string sourceId, string documentId, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync(
                $"""
                 SELECT "source_id","document_id","content_hash","recipe_hash","size_bytes","modified_utc",
                        "extractor_id","chunk_count","status","error","last_run_id","updated_utc"
                 FROM "{DocumentTable}"
                 WHERE "collection" = $c AND "source_id" = $s AND "document_id" = $d
                 """,
                Read,
                [
                    new SqliteParameter("$c", collection),
                    new SqliteParameter("$s", sourceId),
                    new SqliteParameter("$d", documentId),
                ],
                cancellationToken).ConfigureAwait(false);

            return rows.Count == 0 ? null : rows[0];
        }
    }

    /// <summary>Enumerates every state row under one source.</summary>
    public async Task<IReadOnlyList<IngestionDocumentState>> ListDocumentsAsync(
        string collection, string sourceId, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                $"""
                 SELECT "source_id","document_id","content_hash","recipe_hash","size_bytes","modified_utc",
                        "extractor_id","chunk_count","status","error","last_run_id","updated_utc"
                 FROM "{DocumentTable}"
                 WHERE "collection" = $c AND "source_id" = $s
                 ORDER BY "document_id"
                 """,
                Read,
                [new SqliteParameter("$c", collection), new SqliteParameter("$s", sourceId)],
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes one document row, in its own transaction. This is spec 9.5 step d, and it commits
    /// LAST so a torn document is still dirty. A failure here is
    /// <see cref="EdgeErrorCode.IngestionCheckpointWriteFailed"/> (6205) and aborts the run.
    /// </summary>
    public Task UpsertDocumentAsync(
        string collection, IngestionDocumentState row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        return _database.ExecuteInTransactionAsync(
            async (connection, transaction, ct) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     INSERT INTO "{DocumentTable}"
                       ("collection","source_id","document_id","content_hash","recipe_hash","size_bytes",
                        "modified_utc","extractor_id","chunk_count","status","error","last_run_id","updated_utc")
                     VALUES($c,$s,$d,$ch,$rh,$sz,$mt,$ex,$cc,$st,$er,$lr,$up)
                     ON CONFLICT("collection","source_id","document_id") DO UPDATE SET
                       "content_hash"=excluded."content_hash",
                       "recipe_hash"=excluded."recipe_hash",
                       "size_bytes"=excluded."size_bytes",
                       "modified_utc"=excluded."modified_utc",
                       "extractor_id"=excluded."extractor_id",
                       "chunk_count"=excluded."chunk_count",
                       "status"=excluded."status",
                       "error"=excluded."error",
                       "last_run_id"=excluded."last_run_id",
                       "updated_utc"=excluded."updated_utc"
                     """,
                    [
                        new SqliteParameter("$c", collection),
                        new SqliteParameter("$s", row.SourceId),
                        new SqliteParameter("$d", row.DocumentId),
                        Blob("$ch", row.ContentHash),
                        Blob("$rh", row.RecipeHash),
                        Nullable("$sz", row.SizeBytes),
                        Nullable("$mt", Stamp(row.ModifiedUtc)),
                        Nullable("$ex", row.ExtractorId),
                        new SqliteParameter("$cc", row.ChunkCount),
                        new SqliteParameter("$st", row.Status),
                        Nullable("$er", row.Error),
                        Nullable("$lr", row.LastRunId),
                        new SqliteParameter("$up", Stamp(row.UpdatedUtc)!),
                    ],
                    ct).ConfigureAwait(false);

                return true;
            },
            cancellationToken);
    }

    /// <summary>Stamps one row's <c>last_run_id</c> — the hash-gate skip path.</summary>
    public Task StampAsync(
        string collection, string sourceId, string documentId, string runId, CancellationToken cancellationToken) =>
        _database.ExecuteInTransactionAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     UPDATE "{DocumentTable}" SET "last_run_id" = $r
                     WHERE "collection" = $c AND "source_id" = $s AND "document_id" = $d
                     """,
                    [
                        new SqliteParameter("$r", runId),
                        new SqliteParameter("$c", collection),
                        new SqliteParameter("$s", sourceId),
                        new SqliteParameter("$d", documentId),
                    ],
                    ct).ConfigureAwait(false),
            cancellationToken);

    /// <summary>Deletes one document's state row.</summary>
    public Task<int> DeleteDocumentAsync(
        string collection, string sourceId, string documentId, CancellationToken cancellationToken) =>
        _database.ExecuteInTransactionAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     DELETE FROM "{DocumentTable}"
                     WHERE "collection" = $c AND "source_id" = $s AND "document_id" = $d
                     """,
                    [
                        new SqliteParameter("$c", collection),
                        new SqliteParameter("$s", sourceId),
                        new SqliteParameter("$d", documentId),
                    ],
                    ct).ConfigureAwait(false),
            cancellationToken);

    /// <summary>Deletes every state row under one source.</summary>
    public Task<int> DeleteSourceAsync(string collection, string sourceId, CancellationToken cancellationToken) =>
        _database.ExecuteInTransactionAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""DELETE FROM "{DocumentTable}" WHERE "collection" = $c AND "source_id" = $s""",
                    [new SqliteParameter("$c", collection), new SqliteParameter("$s", sourceId)],
                    ct).ConfigureAwait(false),
            cancellationToken);

    /// <summary>Inserts the run row at the start of a run.</summary>
    public Task BeginRunAsync(
        string collection, string runId, string sourceId, ContentHash recipeHash, DateTimeOffset startedUtc,
        CancellationToken cancellationToken) =>
        _database.ExecuteInTransactionAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     INSERT OR REPLACE INTO "{RunTable}"
                       ("run_id","collection","source_id","recipe_hash","started_utc")
                     VALUES($r,$c,$s,$h,$t)
                     """,
                    [
                        new SqliteParameter("$r", runId),
                        new SqliteParameter("$c", collection),
                        new SqliteParameter("$s", sourceId),
                        Blob("$h", recipeHash),
                        new SqliteParameter("$t", Stamp(startedUtc)!),
                    ],
                    ct).ConfigureAwait(false),
            cancellationToken);

    /// <summary>Finishes the run row and trims history to <paramref name="historyLimit"/>.</summary>
    public Task FinishRunAsync(
        string collection,
        string runId,
        IngestionRunOutcome outcome,
        string? suspendReason,
        int documentsIndexed,
        int chunksAdded,
        int chunksRemoved,
        string? error,
        DateTimeOffset finishedUtc,
        int historyLimit,
        CancellationToken cancellationToken) =>
        _database.ExecuteInTransactionAsync(
            async (connection, transaction, ct) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     UPDATE "{RunTable}" SET
                       "finished_utc" = $f, "outcome" = $o, "suspend_reason" = $sr,
                       "documents_indexed" = $di, "chunks_added" = $ca, "chunks_removed" = $cr, "error" = $e
                     WHERE "run_id" = $r
                     """,
                    [
                        new SqliteParameter("$f", Stamp(finishedUtc)!),
                        new SqliteParameter("$o", outcome.ToString()),
                        Nullable("$sr", suspendReason),
                        new SqliteParameter("$di", documentsIndexed),
                        new SqliteParameter("$ca", chunksAdded),
                        new SqliteParameter("$cr", chunksRemoved),
                        Nullable("$e", error),
                        new SqliteParameter("$r", runId),
                    ],
                    ct).ConfigureAwait(false);

                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     DELETE FROM "{RunTable}"
                     WHERE "collection" = $c AND "run_id" NOT IN (
                       SELECT "run_id" FROM "{RunTable}" WHERE "collection" = $c
                       ORDER BY "started_utc" DESC LIMIT $n)
                     """,
                    [new SqliteParameter("$c", collection), new SqliteParameter("$n", historyLimit)],
                    ct).ConfigureAwait(false);

                return true;
            },
            cancellationToken);

    /// <summary>The newest run row for one collection, or null.</summary>
    public async Task<IngestionRunSummary?> LatestRunAsync(string collection, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync(
                $"""
                 SELECT "run_id","outcome","suspend_reason","started_utc"
                 FROM "{RunTable}" WHERE "collection" = $c ORDER BY "started_utc" DESC LIMIT 1
                 """,
                reader => new IngestionRunSummary(
                    reader.GetString(0),
                    reader.IsDBNull(1) || !Enum.TryParse<IngestionRunOutcome>(reader.GetString(1), out var outcome)
                        ? null
                        : outcome,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ParseStamp(reader.GetString(3)) ?? default),
                [new SqliteParameter("$c", collection)],
                cancellationToken).ConfigureAwait(false);

            return rows.Count == 0 ? null : rows[0];
        }
    }

    /// <summary>How many run rows the collection currently keeps. For the trim test.</summary>
    public async Task<int> RunCountAsync(string collection, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return (int)await connection.ScalarAsync<long>(
                $"""SELECT COUNT(*) FROM "{RunTable}" WHERE "collection" = $c""",
                [new SqliteParameter("$c", collection)],
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Spec 12's five count keys, in one round trip.
    /// <para>
    /// <paramref name="currentRecipes"/> is a SET rather than one hash because spec 9.2's extractor
    /// fingerprint is per document: one configuration yields one recipe hash per registered
    /// extractor. A row is recipe-stale when its stored hash matches NONE of them, which is exactly
    /// the population a recipe bump dirtied — comparing against a single hash would report every
    /// PDF stale in a Markdown-and-PDF corpus.
    /// </para>
    /// </summary>
    public async Task<IngestionStateCounts> CountAsync(
        string collection,
        IReadOnlyList<ContentHash> currentRecipes,
        string? latestRunId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentRecipes);

        var parameters = new List<SqliteParameter>
        {
            new("$c", collection),
            new("$indexed", IngestionDocumentStatus.Indexed),
            new("$failed", IngestionDocumentStatus.Failed),
            new("$notext", IngestionDocumentStatus.NoTextLayer),
            Nullable("$run", latestRunId),
        };

        var names = new string[currentRecipes.Count];
        for (var i = 0; i < currentRecipes.Count; i++)
        {
            names[i] = "$r" + i.ToString(CultureInfo.InvariantCulture);
            parameters.Add(Blob(names[i], currentRecipes[i]));
        }

        var staleClause = names.Length == 0
            ? "1"
            : $"""CASE WHEN "recipe_hash" IS NULL OR "recipe_hash" NOT IN ({string.Join(",", names)}) THEN 1 ELSE 0 END""";

        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync(
                $"""
                 SELECT
                   COUNT(*),
                   SUM(CASE WHEN "status" = $indexed THEN 1 ELSE 0 END),
                   SUM(CASE WHEN "status" = $failed THEN 1 ELSE 0 END),
                   SUM(CASE WHEN "status" = $notext THEN 1 ELSE 0 END),
                   SUM(CASE WHEN $run IS NULL OR "last_run_id" IS NULL OR "last_run_id" <> $run THEN 1 ELSE 0 END),
                   SUM({staleClause})
                 FROM "{DocumentTable}" WHERE "collection" = $c
                 """,
                reader => new IngestionStateCounts(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5)),
                parameters,
                cancellationToken).ConfigureAwait(false);

            return rows.Count == 0 ? new IngestionStateCounts(0, 0, 0, 0, 0, 0) : rows[0];
        }
    }

    /// <summary>Rows in the collection's data table.</summary>
    public async Task<long> ChunkCountAsync(string dataTable, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.ScalarAsync<long>(
                $"""SELECT COUNT(*) FROM "{dataTable}" """, parameters: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> MetaAsync(
        SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync(
            $"""SELECT "value" FROM "{MetaTable}" WHERE "key" = $k""",
            reader => reader.GetString(0),
            [new SqliteParameter("$k", key)],
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    private static IngestionDocumentState Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        ReadHash(reader, 2),
        ReadHash(reader, 3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.IsDBNull(5) ? null : ParseStamp(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        ParseStamp(reader.GetString(11)) ?? default);

    private static ContentHash ReadHash(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return ContentHash.Zero;
        }

        var blob = (byte[])reader.GetValue(ordinal);
        return ContentHash.FromBlob(blob);
    }

    private static SqliteParameter Blob(string name, ContentHash hash) =>
        hash.IsZero
            ? new SqliteParameter(name, DBNull.Value)
            : new SqliteParameter(name, hash.ToBlob());

    private static SqliteParameter Nullable(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    private static string? Stamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseStamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
