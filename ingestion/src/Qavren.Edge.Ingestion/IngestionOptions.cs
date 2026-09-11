using Qavren.Edge.VectorData;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// Everything <c>AddIngestion</c> is configured with. Every default here is stated in the plan's
/// "Defaults that change behaviour" heading and asserted by <c>OptionsDefaultsTests</c>.
/// </summary>
/// <remarks>
/// The four <c>FtsMerge*</c> members spec 11 declares are ABSENT (plan adjustment 2): SP3's
/// collection goes through SP2's own <c>AddVectorCollectionMigration</c>, which enters it in
/// <c>EdgeVectorCollectionRegistry</c>, so SP2's bounded FTS5 merge already covers this sidecar
/// and a second merge would be a duplicate. Event id 926 is a matching gap.
/// </remarks>
public sealed class IngestionOptions
{
    /// <summary>
    /// Supplies BOTH the collection's dimensions and the chunk budget. Never an ONNX type.
    /// </summary>
    public ChunkModelProfile Model { get; set; } = ChunkModelProfile.MiniLmL6V2Int8;

    /// <summary>Null takes <see cref="ChunkModelProfile.Dimensions"/>.</summary>
    public int? Dimensions { get; set; }

    /// <summary>The collection, which is also the data table's name, verbatim.</summary>
    public string CollectionName { get; set; } = "chunks";

    /// <summary>The sub-project 1 database name. Null is the unnamed database.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>The keyed vector store and embedding generator. Null is the unkeyed pair.</summary>
    public string? StoreName { get; set; }

    /// <summary>The vector distance function. Cosine by default.</summary>
    public string DistanceFunction { get; set; } = MEVD.DistanceFunction.CosineDistance;

    /// <summary>Emit the FTS5 sidecar over <c>text</c> and <c>heading_path</c>.</summary>
    public bool FullTextIndexed { get; set; } = true;

    /// <summary>
    /// Shapes the collection exactly as SP2's <c>AddVectorCollectionMigration</c> does. MUST carry
    /// the same values passed to <c>AddVectorStore</c>, because SP2's store-level options are NOT
    /// reachable from DI. A mismatch is <c>IngestionCollectionSchemaMismatch</c> (6011) at startup.
    /// </summary>
    public Action<EdgeVectorStoreCollectionOptions>? ConfigureCollection { get; set; }

    /// <summary>
    /// The half of the FTS5 tokenizer decision <see cref="EdgeVectorStoreCollectionOptions"/>
    /// cannot carry: SP2 declares its <c>RemoveDiacritics</c> override <c>internal</c>, so nothing
    /// outside that assembly can set it (plan adjustment 4). 0 | 1 | 2; 2 matches SP2's default
    /// and 6011's remediation names this member.
    /// </summary>
    public int FullTextRemoveDiacritics { get; set; } = 2;

    /// <summary>
    /// Prefixes the three state tables. Quoted into an identifier position, so it is validated
    /// against <c>^[A-Za-z_][A-Za-z0-9_]*$</c> at registration and is 6005 otherwise.
    /// </summary>
    public string StateTablePrefix { get; set; } = "qedge_ingest";

    /// <summary>The unresolved chunk budget. Frozen by the order-400 startup task.</summary>
    public ChunkOptions Chunking { get; } = new();

    /// <summary>Extractor-agnostic extraction knobs.</summary>
    public ExtractionOptions Extraction { get; } = new();

    /// <summary>One of <see cref="ChunkerIds"/>. <see cref="ChunkerIds.Auto"/> by default.</summary>
    public string ChunkerId { get; set; } = ChunkerIds.Auto;

    /// <summary>The source-byte ceiling. 32 MiB; over it is 6052, and the extractor is never reached.</summary>
    public long MaxDocumentBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Chunks per embed + upsert pair (spec 9.5 step a).</summary>
    public int WriteBatchSize { get; set; } = 32;

    /// <summary>Keys per <c>DeleteAsync</c> (spec 9.5 step b).</summary>
    public int DeleteBatchSize { get; set; } = 500;

    /// <summary>
    /// OFF: correctness beats speed. Matching size and mtime is not evidence a file is unchanged —
    /// mtime-preserving copy tools, two-second filesystem granularity, restore-from-backup — and
    /// the hash gate is already one sequential 64 KiB-buffered read.
    /// </summary>
    public bool SkipUnchangedByTimestamp { get; set; }

    /// <summary>Sweep state rows the run did not stamp. Only ever on a <c>Completed</c> run.</summary>
    public bool DeleteMissingDocuments { get; set; } = true;

    /// <summary>
    /// Repair moved ordinals and offsets in place rather than re-embedding. On. Turning it off
    /// avoids the FTS5 <c>_au</c> trigger's per-row delete-plus-insert (spec 9.5).
    /// </summary>
    public bool RepairOrdinals { get; set; } = true;

    /// <summary>Record a document failure and keep going.</summary>
    public bool ContinueOnDocumentError { get; set; } = true;

    /// <summary>Consecutive document failures that abort the run with 6010.</summary>
    public int AbortAfterConsecutiveErrors { get; set; } = 20;

    /// <summary>
    /// Throw <c>IngestionRecipeChanged</c> (6009) on the first drifted document instead of
    /// re-indexing. Off; the default logs event 915 once and re-indexes.
    /// </summary>
    public bool StrictRecipe { get; set; }

    /// <summary>What a throttle pause means.</summary>
    public ThrottlePauseBehavior PauseBehavior { get; set; } = ThrottlePauseBehavior.Suspend;

    /// <summary>
    /// How long <c>Sleeping</c> waits for the runner's next committed checkpoint. Validated at or
    /// under 2 s, because SP1's platform bridges raise this on the callback thread against iOS's
    /// documented ~5 s window and Android's <c>OnPause</c> ANR path.
    /// </summary>
    public TimeSpan SleepGraceBudget { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>How long <c>Stopping</c> waits for the same checkpoint.</summary>
    public TimeSpan StopGraceBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Rows kept in <c>&lt;prefix&gt;_run</c>. Trimmed at the end of every run.</summary>
    public int RunHistoryLimit { get; set; } = 20;

    /// <summary>
    /// The five counting queries in <c>Describe()</c>. Off: <c>Describe()</c> is synchronous and
    /// runs on every diagnostics report. When off the keys are present with the value
    /// <c>"(disabled)"</c>, never omitted, so nobody reads a missing key as zero.
    /// </summary>
    public bool IncludeCountsInDiagnostics { get; set; }

    /// <summary>Consumer extractors, ahead of the built-ins in registration order.</summary>
    public IList<IDocumentExtractor> Extractors { get; } = [];
}
