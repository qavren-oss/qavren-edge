using System.Globalization;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// The two schemas SP3 owns: the chunk collection's <c>VectorStoreCollectionDefinition</c> and the
/// three state tables.
/// </summary>
public static class IngestionSchema
{
    /// <summary>The state schema version stored in <c>&lt;prefix&gt;_meta.schema_version</c>.</summary>
    public const int StateSchemaVersion = 1;

    /// <summary>The <c>&lt;prefix&gt;_meta</c> key holding <see cref="StateSchemaVersion"/>.</summary>
    public const string MetaSchemaVersionKey = "schema_version";

    /// <summary>The <c>&lt;prefix&gt;_meta</c> key holding <see cref="ContentHash.AlgorithmId"/>.</summary>
    public const string MetaHashAlgorithmKey = "hash_algorithm";

    /// <summary>
    /// Builds the chunk collection's definition programmatically — no attributed record type, no
    /// reflection, so the dimension is a runtime value and 384-dim MiniLM and 768-dim nomic share
    /// one code path.
    /// <para>
    /// The vector property is declared <see cref="ReadOnlyMemory{T}"/> of <see cref="float"/>,
    /// <b>not</b> a <see cref="string"/> source, because SP3 resolves every vector itself (spec 9.5
    /// step a1). That single decision is what makes <c>EmbedCalls</c> countable and 6206 separable
    /// from 6207.
    /// </para>
    /// </summary>
    /// <param name="dimensions">The vector width.</param>
    /// <param name="distanceFunction">One of MEVD's <c>DistanceFunction</c> constants.</param>
    /// <param name="fullTextIndexed">Mark <c>text</c> and <c>heading_path</c> full-text indexed.</param>
    /// <returns>The definition.</returns>
    public static MEVD.VectorStoreCollectionDefinition BuildDefinition(
        int dimensions, string distanceFunction, bool fullTextIndexed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(distanceFunction);

        return new MEVD.VectorStoreCollectionDefinition
        {
            Properties =
            [
                new MEVD.VectorStoreKeyProperty(IngestionColumns.Key, typeof(string)),
                new MEVD.VectorStoreVectorProperty(IngestionColumns.Embedding, typeof(ReadOnlyMemory<float>), dimensions)
                {
                    DistanceFunction = distanceFunction,
                },
                new MEVD.VectorStoreDataProperty(IngestionColumns.SourceId, typeof(string)) { IsIndexed = true },
                new MEVD.VectorStoreDataProperty(IngestionColumns.DocumentId, typeof(string)) { IsIndexed = true },
                new MEVD.VectorStoreDataProperty(IngestionColumns.Ordinal, typeof(int)),
                new MEVD.VectorStoreDataProperty(IngestionColumns.Text, typeof(string))
                {
                    IsFullTextIndexed = fullTextIndexed,
                },
                new MEVD.VectorStoreDataProperty(IngestionColumns.HeadingPath, typeof(string))
                {
                    IsFullTextIndexed = fullTextIndexed,
                },
                new MEVD.VectorStoreDataProperty(IngestionColumns.ContentHash, typeof(byte[])),
                new MEVD.VectorStoreDataProperty(IngestionColumns.CharStart, typeof(int)),
                new MEVD.VectorStoreDataProperty(IngestionColumns.CharEnd, typeof(int)),
                new MEVD.VectorStoreDataProperty(IngestionColumns.TokenCount, typeof(int)),
                new MEVD.VectorStoreDataProperty(IngestionColumns.Page, typeof(int)),
                new MEVD.VectorStoreDataProperty(IngestionColumns.BlockKind, typeof(string)),
                new MEVD.VectorStoreDataProperty(IngestionColumns.ExtractorId, typeof(string)),
                new MEVD.VectorStoreDataProperty(IngestionColumns.MediaType, typeof(string)),
                new MEVD.VectorStoreDataProperty(IngestionColumns.CreatedUtc, typeof(DateTimeOffset)),
            ],
        };
    }

    /// <summary>
    /// The three state tables and their indexes, as a statement list — the same shape
    /// <c>EdgeVectorSchema.BuildCreateSql()</c> returns.
    /// <para>
    /// All three are <c>WITHOUT ROWID</c>, including the two whose key is a single column (plan
    /// adjustment 19): the row lives in the key's b-tree rather than a rowid table plus a separate
    /// unique index, which is the right shape for tables read by primary key, never scanned by
    /// rowid, and holding twenty rows and two rows. The consequence is that every row must supply
    /// its primary key and there is no <c>AUTOINCREMENT</c> — both already true.
    /// </para>
    /// </summary>
    /// <param name="tablePrefix">
    /// <c>IngestionOptions.StateTablePrefix</c>. Quoted into an identifier position, so
    /// <c>AddIngestion</c> validates it against <c>^[A-Za-z_][A-Za-z0-9_]*$</c> first.
    /// </param>
    /// <returns>The statements, in apply order.</returns>
    public static IReadOnlyList<string> BuildStateSql(string tablePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePrefix);

        var p = tablePrefix;
        return
        [
            string.Format(
                CultureInfo.InvariantCulture,
                """
                CREATE TABLE IF NOT EXISTS "{0}_document" (
                  "collection"    TEXT NOT NULL,
                  "source_id"     TEXT NOT NULL,
                  "document_id"   TEXT NOT NULL,
                  "content_hash"  BLOB,
                  "recipe_hash"   BLOB,
                  "size_bytes"    INTEGER,
                  "modified_utc"  TEXT,
                  "extractor_id"  TEXT,
                  "chunk_count"   INTEGER NOT NULL DEFAULT 0,
                  "status"        TEXT NOT NULL,
                  "error"         TEXT,
                  "last_run_id"   TEXT,
                  "updated_utc"   TEXT NOT NULL,
                  PRIMARY KEY ("collection", "source_id", "document_id")
                ) WITHOUT ROWID
                """,
                p),
            string.Format(
                CultureInfo.InvariantCulture,
                """CREATE INDEX IF NOT EXISTS "{0}_document_last_run" ON "{0}_document"("collection", "source_id", "last_run_id")""",
                p),
            string.Format(
                CultureInfo.InvariantCulture,
                """CREATE INDEX IF NOT EXISTS "{0}_document_recipe" ON "{0}_document"("collection", "recipe_hash")""",
                p),
            string.Format(
                CultureInfo.InvariantCulture,
                """
                CREATE TABLE IF NOT EXISTS "{0}_run" (
                  "run_id"            TEXT PRIMARY KEY,
                  "collection"        TEXT NOT NULL,
                  "source_id"         TEXT NOT NULL,
                  "recipe_hash"       BLOB,
                  "started_utc"       TEXT NOT NULL,
                  "finished_utc"      TEXT,
                  "outcome"           TEXT,
                  "suspend_reason"    TEXT,
                  "documents_indexed" INTEGER NOT NULL DEFAULT 0,
                  "chunks_added"      INTEGER NOT NULL DEFAULT 0,
                  "chunks_removed"    INTEGER NOT NULL DEFAULT 0,
                  "error"             TEXT
                ) WITHOUT ROWID
                """,
                p),
            string.Format(
                CultureInfo.InvariantCulture,
                """CREATE INDEX IF NOT EXISTS "{0}_run_started" ON "{0}_run"("collection", "started_utc" DESC)""",
                p),
            string.Format(
                CultureInfo.InvariantCulture,
                """
                CREATE TABLE IF NOT EXISTS "{0}_meta" (
                  "key"   TEXT PRIMARY KEY,
                  "value" TEXT NOT NULL
                ) WITHOUT ROWID
                """,
                p),
        ];
    }
}
