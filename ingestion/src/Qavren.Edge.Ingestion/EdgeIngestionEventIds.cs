namespace Qavren.Edge.Ingestion;

/// <summary>
/// Every log event Qavren.Edge.Ingestion emits, at spec section 11's numbers.
/// </summary>
/// <remarks>
/// 926 (<c>FtsMergeCompleted</c>) is deliberately ABSENT — plan adjustment 2 deleted SP3's own
/// FTS5 sidecar merge along with the four <c>FtsMerge*</c> members of <c>IngestionOptions</c>.
/// The gap is left rather than closed by renumbering 927, so a log filter written against the
/// published spec keeps matching every id that still exists.
/// </remarks>
public static class EdgeIngestionEventIds
{
    /// <summary>A run began.</summary>
    public const int RunStarted = 900;

    /// <summary>A run finished successfully.</summary>
    public const int RunCompleted = 901;

    /// <summary>A run suspended after a failure it could not retry past (see <see cref="EdgeErrorCode.IngestionEmbeddingFailed"/> / <see cref="EdgeErrorCode.IngestionRunAborted"/>).</summary>
    public const int RunSuspended = 902;

    /// <summary>A run failed outright.</summary>
    public const int RunFailed = 903;

    /// <summary>A document was skipped — its content hash matched the last indexed run.</summary>
    public const int DocumentSkipped = 904;

    /// <summary>A document was extracted, chunked and written successfully.</summary>
    public const int DocumentIndexed = 905;

    /// <summary>A document had no text layer (see <see cref="EdgeErrorCode.DocumentHasNoTextLayer"/>).</summary>
    public const int DocumentNoTextLayer = 906;

    /// <summary>A document failed extraction, chunking or writing.</summary>
    public const int DocumentFailed = 907;

    /// <summary>A document's extension matched no registered extractor (see <see cref="EdgeErrorCode.ExtractorNotFound"/>).</summary>
    public const int DocumentUnsupported = 908;

    /// <summary>Logged once per document naming which <c>IDocumentExtractor</c> was selected.</summary>
    public const int ExtractorSelected = 909;

    /// <summary>A chunk's heading-path breadcrumb was truncated to fit <c>HeadingPathTokenBudget</c>.</summary>
    public const int HeadingPathTruncated = 910;

    /// <summary>A trailing under-<c>MinTokens</c> chunk was merged into the previous one instead of being emitted alone.</summary>
    public const int ChunkMergedUp = 911;

    /// <summary>Stale documents were pruned at the end of a run (a source no longer yields them).</summary>
    public const int StaleDocumentsPruned = 912;

    /// <summary>Extraction switched to streaming mode for a large document.</summary>
    public const int ExtractionStreamingMode = 913;

    /// <summary>Invalid UTF-8 fell back to Latin-1 decoding (see <see cref="EdgeErrorCode.DocumentEncodingUndecodable"/>).</summary>
    public const int EncodingFallback = 914;

    /// <summary>The recipe hash changed and documents were silently re-indexed under it (see <see cref="EdgeErrorCode.IngestionRecipeChanged"/>).</summary>
    public const int RecipeChanged = 915;

    /// <summary>Chunk ordinals for a document were renumbered to close a gap left by a partial write.</summary>
    public const int OrdinalsRepaired = 916;

    /// <summary>Embedded images were dropped from extracted content; text-only indexing continued.</summary>
    public const int MediImagesDropped = 917;

    /// <summary>An embed batch was halved and retried after <see cref="EdgeErrorCode.IngestionEmbeddingFailed"/>.</summary>
    public const int EmbedBatchShrunk = 918;

    /// <summary>The run's throttle interval was adjusted in response to device conditions.</summary>
    public const int ThrottleAdjusted = 919;

    /// <summary>The run paused briefly under the throttle.</summary>
    public const int ThrottlePaused = 920;

    /// <summary>A chunk's text was truncated to fit <c>MaxTokens</c>.</summary>
    public const int ChunkTruncated = 921;

    /// <summary>The configured chunker failed and the run fell back to a simpler one.</summary>
    public const int ChunkerFellBack = 922;

    /// <summary>An extractor reported a non-fatal warning while extracting a document.</summary>
    public const int ExtractionWarning = 923;

    /// <summary>A PDF page's parse time hit the page budget (see <see cref="EdgeErrorCode.DocumentPageBudgetExceeded"/>).</summary>
    public const int PageTimedOut = 924;

    /// <summary>A checkpoint was committed to the state store.</summary>
    public const int StateCommitted = 925;

    // 926 is a gap. See the remarks above.
    /// <summary>A heading was sanitised before being joined into a breadcrumb (see <see cref="IngestionColumns.SanitizeHeading"/>).</summary>
    public const int HeadingSanitised = 927;
}
