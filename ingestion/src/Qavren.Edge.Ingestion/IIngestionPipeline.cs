namespace Qavren.Edge.Ingestion;

/// <summary>
/// The one runtime surface. Resolved from DI after <c>AddIngestion</c>.
/// </summary>
public interface IIngestionPipeline
{
    /// <summary>Runs one source into one collection.</summary>
    /// <param name="source">Where the documents come from.</param>
    /// <param name="collectionName">
    /// Null resolves to the single configured collection; an unknown name, or null with more than
    /// one configured collection, is <see cref="EdgeErrorCode.IngestionCollectionNotConfigured"/>
    /// (6002) before any enumeration, any open and any write.
    /// </param>
    /// <param name="options">Per-run overrides. Null takes every default.</param>
    /// <param name="cancellationToken">Cancels the run on a committed boundary.</param>
    /// <returns>The run's counters and per-document results.</returns>
    Task<IngestionRunResult> RunAsync(
        IngestionSource source,
        string? collectionName = null,
        IngestionRunOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>The progress-reporting convenience overload.</summary>
    /// <param name="source">Where the documents come from.</param>
    /// <param name="progress">Invoked at most once per 100 ms plus once per document boundary.</param>
    /// <param name="cancellationToken">Cancels the run on a committed boundary.</param>
    /// <returns>The run's counters and per-document results.</returns>
    Task<IngestionRunResult> RunAsync(
        IngestionSource source,
        IProgress<IngestionProgress> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Full enumeration and sweep, no write phase. The answer to "a budgeted run never prunes"
    /// (spec 9.6).
    /// </summary>
    /// <param name="source">The source whose live document ids define what is NOT stale.</param>
    /// <param name="collectionName">Null resolves to the single configured collection.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>The number of documents pruned.</returns>
    Task<int> PruneAsync(
        IngestionSource source, string? collectionName = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes every chunk and state row under one source.</summary>
    /// <param name="sourceId">The source id.</param>
    /// <param name="collectionName">Null resolves to the single configured collection.</param>
    /// <param name="ct">Cancels the removal.</param>
    /// <returns>The number of documents removed.</returns>
    Task<int> RemoveSourceAsync(string sourceId, string? collectionName = null, CancellationToken ct = default);

    /// <summary>Deletes every chunk and the state row for one document.</summary>
    /// <param name="sourceId">The source id.</param>
    /// <param name="documentId">The document id.</param>
    /// <param name="collectionName">Null resolves to the single configured collection.</param>
    /// <param name="ct">Cancels the removal.</param>
    /// <returns>1 when the document existed, 0 otherwise.</returns>
    Task<int> RemoveDocumentAsync(
        string sourceId, string documentId, string? collectionName = null, CancellationToken ct = default);

    /// <summary>The collection's current state, counted (spec 12).</summary>
    /// <param name="collectionName">Null resolves to the single configured collection.</param>
    /// <param name="ct">Cancels the counting queries.</param>
    /// <returns>The status.</returns>
    Task<IngestionStatus> GetStatusAsync(string? collectionName = null, CancellationToken ct = default);

    /// <summary>The frozen recipe this pipeline embeds against.</summary>
    /// <param name="ct">Cancels the resolution.</param>
    /// <returns>The recipe.</returns>
    ValueTask<IngestionRecipe> GetRecipeAsync(CancellationToken ct = default);

    /// <summary>The most recent progress, or null when no run is active.</summary>
    IngestionProgress? Current { get; }

    /// <summary>Asks the active run to stop on its next committed boundary.</summary>
    /// <param name="reason">
    /// Becomes <see cref="IngestionRunResult.SuspendReason"/>. <c>caller:stop</c> by convention.
    /// </param>
    void RequestStop(string reason);
}

/// <summary>
/// Per-run overrides. Every member is null-or-false by design; each default is stated in the
/// plan's "Defaults that change behaviour" heading for Task 5.1 and asserted by a test.
/// </summary>
public sealed class IngestionRunOptions
{
    /// <summary>Null is <see cref="IngestionBudget.Unlimited"/>: the run never suspends for time.</summary>
    public IngestionBudget? Budget { get; set; }

    /// <summary>Null means no callbacks.</summary>
    public IProgress<IngestionProgress>? Progress { get; set; }

    /// <summary>
    /// Bypasses BOTH gates — timestamp and hash — and re-extracts, re-chunks and re-diffs every
    /// document. It does <b>not</b> bypass the diff itself, so an unchanged document still writes
    /// nothing: <c>Force</c> costs CPU, never embeddings. The name invites the opposite reading,
    /// which is why it is said here.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// Null falls back to <see cref="IngestionOptions.DeleteMissingDocuments"/> (true). A nullable
    /// bool rather than a bool is deliberate: <c>false</c> must be distinguishable from "not
    /// specified", or a per-run override could never turn pruning OFF for a run against options
    /// that have it on.
    /// </summary>
    public bool? DeleteMissing { get; set; }

    /// <summary>
    /// Promotes the first document failure to a run failure and rethrows the original, without
    /// mutating the registered <see cref="IngestionOptions.ContinueOnDocumentError"/>.
    /// </summary>
    public bool FailFast { get; set; }

    /// <summary>
    /// Off: cancellation surfaces as <see cref="IngestionRunOutcome.Cancelled"/> with no exception,
    /// because a cancelled run is a normal outcome carrying counters a caller wants to read and an
    /// <see cref="OperationCanceledException"/> throws those counters away (spec 13.3).
    /// </summary>
    public bool ThrowOnCancellation { get; set; }

    /// <summary>Null takes <see cref="IngestionOptions.WriteBatchSize"/> (32).</summary>
    public int? WriteBatchSize { get; set; }

    /// <summary>Null considers every enumerated item.</summary>
    public Func<DocumentSourceItem, bool>? Filter { get; set; }
}

/// <summary>The per-document verdict. The same values are persisted as the state row's status.</summary>
public enum IngestionDocumentOutcome
{
    /// <summary>Unchanged on the timestamp or hash gate. No decode, no parse, no embed.</summary>
    Skipped,

    /// <summary>Chunks were written, removed, repaired or all three.</summary>
    Indexed,

    /// <summary>Pruned: the source no longer yields it.</summary>
    Removed,

    /// <summary>Parsed, but carried no extractable text (6105).</summary>
    NoTextLayer,

    /// <summary>No extractor accepted it (6101).</summary>
    Unsupported,

    /// <summary>A document-tier fault. The result's <c>Failure</c> carries the code.</summary>
    Failed,
}

/// <summary>The state table's <c>status</c> values, whose spelling is this enum's names.</summary>
public static class IngestionDocumentStatus
{
    /// <summary><see cref="IngestionDocumentOutcome.Indexed"/>.</summary>
    public const string Indexed = nameof(IngestionDocumentOutcome.Indexed);

    /// <summary><see cref="IngestionDocumentOutcome.NoTextLayer"/>.</summary>
    public const string NoTextLayer = nameof(IngestionDocumentOutcome.NoTextLayer);

    /// <summary><see cref="IngestionDocumentOutcome.Unsupported"/>.</summary>
    public const string Unsupported = nameof(IngestionDocumentOutcome.Unsupported);

    /// <summary><see cref="IngestionDocumentOutcome.Failed"/>.</summary>
    public const string Failed = nameof(IngestionDocumentOutcome.Failed);

    /// <summary>
    /// True for a status the next run may skip on an unchanged hash. <c>Failed</c> and
    /// <c>NoTextLayer</c> rows are KEPT so the next run skips them rather than retrying a broken
    /// file forever — and a recipe bump retries them automatically, which is the correct trigger.
    /// <para>
    /// <b>Internal.</b> Spec 11 declares <see cref="IngestionDocumentStatus"/> as four consts and
    /// nothing else, so this stays off the public surface the wave-9 API gate reads. The runner's
    /// two skip gates are its only callers.
    /// </para>
    /// </summary>
    /// <param name="status">The stored status.</param>
    /// <returns>True when the status is terminal.</returns>
    internal static bool IsTerminal(string? status) =>
        status is Indexed or NoTextLayer or Unsupported or Failed;
}

/// <summary>What one document cost and produced.</summary>
/// <param name="DocumentId">The document id.</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="ExtractorId">The selected extractor, when one was selected.</param>
/// <param name="ChunksAdded">Chunks upserted.</param>
/// <param name="ChunksRemoved">Chunks deleted.</param>
/// <param name="ChunksUnchanged">Chunks found already present by hash.</param>
/// <param name="ChunksRepaired">Chunks whose ordinal or offsets moved.</param>
/// <param name="TokensEmbedded">Tokens this document cost.</param>
/// <param name="Duration">Wall time.</param>
/// <param name="Failure">The document-tier fault, or null.</param>
public sealed record IngestionDocumentResult(
    string DocumentId,
    IngestionDocumentOutcome Outcome,
    string? ExtractorId,
    int ChunksAdded,
    int ChunksRemoved,
    int ChunksUnchanged,
    int ChunksRepaired,
    long TokensEmbedded,
    TimeSpan Duration,
    IngestionFailure? Failure);

/// <summary>What a run did. A suspension always lands on a committed boundary (spec 10.2).</summary>
/// <param name="RunId">The run id, also the state rows' <c>last_run_id</c>.</param>
/// <param name="CollectionName">The collection.</param>
/// <param name="SourceId">The source.</param>
/// <param name="Outcome">How the run ended.</param>
/// <param name="SuspendReason">One of spec 10.2's reason strings, or null.</param>
/// <param name="DocumentsSeen">Enumerated, duplicates and skips included.</param>
/// <param name="DocumentsSkipped">Unchanged on a gate.</param>
/// <param name="DocumentsIndexed">Written.</param>
/// <param name="DocumentsRemoved">Pruned.</param>
/// <param name="DocumentsFailed">Recorded <c>Failed</c>.</param>
/// <param name="ChunksAdded">Chunks upserted.</param>
/// <param name="ChunksRemoved">Chunks deleted.</param>
/// <param name="ChunksUnchanged">Chunks already present by hash.</param>
/// <param name="ChunksRepaired">Chunks whose ordinal or offsets moved.</param>
/// <param name="TokensEmbedded">Tokens the run cost, as spec 9.5 defines them.</param>
/// <param name="EmbedCalls">Spec 9.5 step a1 calls, exactly.</param>
/// <param name="Duration">Wall time.</param>
/// <param name="RecipeHash">The recipe every document was measured against.</param>
/// <param name="Documents">The per-document results, in processing order.</param>
/// <param name="Failure">The single RUN-level fault; null on a Completed or Suspended run.</param>
public sealed record IngestionRunResult(
    string RunId,
    string CollectionName,
    string SourceId,
    IngestionRunOutcome Outcome,
    string? SuspendReason,
    int DocumentsSeen,
    int DocumentsSkipped,
    int DocumentsIndexed,
    int DocumentsRemoved,
    int DocumentsFailed,
    int ChunksAdded,
    int ChunksRemoved,
    int ChunksUnchanged,
    int ChunksRepaired,
    long TokensEmbedded,
    int EmbedCalls,
    TimeSpan Duration,
    string RecipeHash,
    IReadOnlyList<IngestionDocumentResult> Documents,
    IngestionFailure? Failure)
{
    /// <summary>
    /// The per-document failures, projected from <see cref="Documents"/>. A computed view, not a
    /// second list: one source of truth, and <see cref="Failure"/> stays what it says it is — the
    /// single RUN-level fault.
    /// </summary>
    public IEnumerable<IngestionDocumentResult> Failures =>
        Documents.Where(d => d.Outcome is IngestionDocumentOutcome.Failed);
}

/// <summary>What the collection currently holds (spec 12).</summary>
/// <param name="CollectionName">The collection.</param>
/// <param name="RecipeHash">The recipe this build embeds against.</param>
/// <param name="DocumentCount">State rows.</param>
/// <param name="IndexedCount">State rows whose status is <c>Indexed</c>.</param>
/// <param name="FailedCount">State rows whose status is <c>Failed</c>.</param>
/// <param name="NoTextLayerCount">State rows whose status is <c>NoTextLayer</c>.</param>
/// <param name="StaleDocumentCount">
/// Rows whose <c>last_run_id</c> is not the newest completed run's: candidates for a sweep.
/// </param>
/// <param name="RecipeStaleCount">
/// Rows whose stored <c>recipe_hash</c> differs from the current recipe — one indexed COUNT, and
/// the ONLY sense of "outstanding" that is cheap to compute. Content drift on disk is NOT counted:
/// establishing it means opening and streaming every file, which is a run.
/// </param>
/// <param name="ChunkCount">Rows in the collection's data table.</param>
/// <param name="LastRunId">The newest run's id.</param>
/// <param name="LastOutcome">Its outcome.</param>
/// <param name="LastSuspendReason">Its suspend reason.</param>
/// <param name="LastRunUtc">When it started.</param>
public sealed record IngestionStatus(
    string CollectionName,
    string RecipeHash,
    int DocumentCount,
    int IndexedCount,
    int FailedCount,
    int NoTextLayerCount,
    int StaleDocumentCount,
    int RecipeStaleCount,
    long ChunkCount,
    string? LastRunId,
    IngestionRunOutcome? LastOutcome,
    string? LastSuspendReason,
    DateTimeOffset? LastRunUtc);
