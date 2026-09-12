namespace Qavren.Edge.Ingestion;

/// <summary>How a run ended.</summary>
public enum IngestionRunOutcome
{
    /// <summary>Every enumerated document was processed. The only outcome that prunes.</summary>
    Completed,

    /// <summary>A budget, a throttle pause or a lifecycle stop ended the run on a committed boundary.</summary>
    Suspended,

    /// <summary>The caller's token was cancelled, or <c>RequestStop</c> was called.</summary>
    Cancelled,

    /// <summary>A run-tier fault. <c>IngestionRunResult.Failure</c> carries it.</summary>
    Failed,
}

/// <summary>What a run is doing right now. Drives a foreground notification's text (spec 10.4).</summary>
public enum IngestionStage
{
    /// <summary>Walking the source.</summary>
    Enumerating,

    /// <summary>Inside an extractor.</summary>
    Extracting,

    /// <summary>Inside a chunker.</summary>
    Chunking,

    /// <summary>Inside spec 9.5 step a1.</summary>
    Embedding,

    /// <summary>Inside spec 9.5 step a2 or b.</summary>
    Writing,

    /// <summary>Inside spec 9.5 step c.</summary>
    Repairing,

    /// <summary>Inside spec 9.6.</summary>
    Pruning,
}

/// <summary>
/// Counts, never a percentage: enumeration is streaming, so the denominator is unknown until the
/// run ends and a fabricated percentage is worse than none (spec 10.4).
/// </summary>
/// <param name="RunId">The run this progress belongs to.</param>
/// <param name="Stage">What the run is doing.</param>
/// <param name="DocumentsSeen">Enumerated so far, duplicates and skips included.</param>
/// <param name="DocumentsIndexed">Written so far.</param>
/// <param name="DocumentsSkipped">Unchanged on the timestamp or hash gate.</param>
/// <param name="DocumentsFailed">Recorded <c>Failed</c>.</param>
/// <param name="ChunksAdded">Upserted so far.</param>
/// <param name="ChunksRemoved">Deleted so far.</param>
/// <param name="CurrentDocumentId">The document in hand, or null between documents.</param>
/// <param name="CurrentPage">The page in hand, for paged formats.</param>
/// <param name="EffectiveWriteBatchSize">The batch after throttle and memory pressure.</param>
public readonly record struct IngestionProgress(
    string RunId,
    IngestionStage Stage,
    int DocumentsSeen,
    int DocumentsIndexed,
    int DocumentsSkipped,
    int DocumentsFailed,
    int ChunksAdded,
    int ChunksRemoved,
    string? CurrentDocumentId,
    int? CurrentPage,
    int EffectiveWriteBatchSize);
