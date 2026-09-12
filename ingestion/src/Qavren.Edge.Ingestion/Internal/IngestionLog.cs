using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Every event id the runtime emits, through source-generated
/// <see cref="LoggerMessage"/> delegates — CA1848 is an error under
/// <c>TreatWarningsAsErrors</c>, so there is no interpolated logging call anywhere in SP3.
/// <para>
/// <b>No document text is ever logged.</b> Ids, paths, offsets and counts only, and
/// <c>NoDocumentTextInLogsTests</c> asserts it by capturing every line during an ingest and failing
/// if any contains a substring of the file.
/// </para>
/// </summary>
internal static class IngestionLog
{
    private static readonly Action<ILogger, string, string, string, Exception?> RunStartedCore =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            Id(EdgeIngestionEventIds.RunStarted, nameof(EdgeIngestionEventIds.RunStarted)),
            "Ingestion run {RunId} started over source '{SourceId}' into collection '{Collection}'.");

    private static readonly Action<ILogger, string, int, int, int, int, Exception?> RunCompletedCore =
        LoggerMessage.Define<string, int, int, int, int>(
            LogLevel.Information,
            Id(EdgeIngestionEventIds.RunCompleted, nameof(EdgeIngestionEventIds.RunCompleted)),
            "Ingestion run {RunId} completed: {Indexed} indexed, {Skipped} skipped, {Failed} failed, {ChunksAdded} chunks added.");

    private static readonly Action<ILogger, string, string, Exception?> RunSuspendedCore =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            Id(EdgeIngestionEventIds.RunSuspended, nameof(EdgeIngestionEventIds.RunSuspended)),
            "Ingestion run {RunId} suspended on a committed boundary: {Reason}.");

    private static readonly Action<ILogger, string, string, Exception?> RunFailedCore =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            Id(EdgeIngestionEventIds.RunFailed, nameof(EdgeIngestionEventIds.RunFailed)),
            "Ingestion run {RunId} failed: {Reason}.");

    private static readonly Action<ILogger, string, string, Exception?> DocumentSkippedCore =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            Id(EdgeIngestionEventIds.DocumentSkipped, nameof(EdgeIngestionEventIds.DocumentSkipped)),
            "Document '{DocumentId}' skipped on the {Gate} gate.");

    private static readonly Action<ILogger, string, int, int, int, Exception?> DocumentIndexedCore =
        LoggerMessage.Define<string, int, int, int>(
            LogLevel.Debug,
            Id(EdgeIngestionEventIds.DocumentIndexed, nameof(EdgeIngestionEventIds.DocumentIndexed)),
            "Document '{DocumentId}' indexed: {Added} added, {Removed} removed, {Repaired} repaired.");

    private static readonly Action<ILogger, string, string, Exception?> DocumentNoTextLayerCore =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            Id(EdgeIngestionEventIds.DocumentNoTextLayer, nameof(EdgeIngestionEventIds.DocumentNoTextLayer)),
            "Document '{DocumentId}' carries no text layer; extractor '{ExtractorId}'.");

    private static readonly Action<ILogger, string, int, string, Exception?> DocumentFailedCore =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Error,
            Id(EdgeIngestionEventIds.DocumentFailed, nameof(EdgeIngestionEventIds.DocumentFailed)),
            "Document '{DocumentId}' failed with {Code}: {Reason}.");

    private static readonly Action<ILogger, string, string, Exception?> DocumentUnsupportedCore =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            Id(EdgeIngestionEventIds.DocumentUnsupported, nameof(EdgeIngestionEventIds.DocumentUnsupported)),
            "No extractor accepted document '{DocumentId}' (media type '{MediaType}').");

    private static readonly Action<ILogger, string, string, int, Exception?> ExtractorSelectedCore =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Debug,
            Id(EdgeIngestionEventIds.ExtractorSelected, nameof(EdgeIngestionEventIds.ExtractorSelected)),
            "Document '{DocumentId}' handed to extractor '{ExtractorId}' v{Version}.");

    private static readonly Action<ILogger, string, int, Exception?> StaleDocumentsPrunedCore =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            Id(EdgeIngestionEventIds.StaleDocumentsPruned, nameof(EdgeIngestionEventIds.StaleDocumentsPruned)),
            "Source '{SourceId}': {Count} stale documents pruned.");

    private static readonly Action<ILogger, string, string, Exception?> RecipeChangedCore =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            Id(EdgeIngestionEventIds.RecipeChanged, nameof(EdgeIngestionEventIds.RecipeChanged)),
            "The ingestion recipe changed from {StoredHash} to {CurrentHash}; drifted documents will be re-indexed.");

    private static readonly Action<ILogger, string, int, Exception?> OrdinalsRepairedCore =
        LoggerMessage.Define<string, int>(
            LogLevel.Debug,
            Id(EdgeIngestionEventIds.OrdinalsRepaired, nameof(EdgeIngestionEventIds.OrdinalsRepaired)),
            "Document '{DocumentId}': {Count} chunk ordinals repaired in place.");

    private static readonly Action<ILogger, int, int, string, Exception?> EmbedBatchShrunkCore =
        LoggerMessage.Define<int, int, string>(
            LogLevel.Warning,
            Id(EdgeIngestionEventIds.EmbedBatchShrunk, nameof(EdgeIngestionEventIds.EmbedBatchShrunk)),
            "Write batch shrunk from {From} to {To}: {Reason}.");

    private static readonly Action<ILogger, int, int, string, Exception?> ThrottleAdjustedCore =
        LoggerMessage.Define<int, int, string>(
            LogLevel.Debug,
            Id(EdgeIngestionEventIds.ThrottleAdjusted, nameof(EdgeIngestionEventIds.ThrottleAdjusted)),
            "Throttle adjusted the write batch from {From} to {To}: {Reason}.");

    private static readonly Action<ILogger, string, Exception?> ThrottlePausedCore =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            Id(EdgeIngestionEventIds.ThrottlePaused, nameof(EdgeIngestionEventIds.ThrottlePaused)),
            "Throttle paused ingestion: {Reason}.");

    private static readonly Action<ILogger, string, string, Exception?> StateCommittedCore =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            Id(EdgeIngestionEventIds.StateCommitted, nameof(EdgeIngestionEventIds.StateCommitted)),
            "State row committed for '{DocumentId}' with status {Status}.");

    private static readonly Action<ILogger, string, Exception?> GraceWindowMissedCore =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            Id(EdgeIngestionEventIds.RunSuspended, nameof(EdgeIngestionEventIds.RunSuspended)),
            "The lifecycle grace window elapsed before the runner reached a committed checkpoint: {Phase}.");

    public static void RunStarted(ILogger logger, string runId, string sourceId, string collection) =>
        RunStartedCore(logger, runId, sourceId, collection, null);

    public static void RunCompleted(
        ILogger logger, string runId, int indexed, int skipped, int failed, int chunksAdded) =>
        RunCompletedCore(logger, runId, indexed, skipped, failed, chunksAdded, null);

    public static void RunSuspended(ILogger logger, string runId, string reason) =>
        RunSuspendedCore(logger, runId, reason, null);

    public static void RunFailed(ILogger logger, string runId, string reason, Exception? error) =>
        RunFailedCore(logger, runId, reason, error);

    public static void DocumentSkipped(ILogger logger, string documentId, string gate) =>
        DocumentSkippedCore(logger, documentId, gate, null);

    public static void DocumentIndexed(ILogger logger, string documentId, int added, int removed, int repaired) =>
        DocumentIndexedCore(logger, documentId, added, removed, repaired, null);

    public static void DocumentNoTextLayer(ILogger logger, string documentId, string extractorId) =>
        DocumentNoTextLayerCore(logger, documentId, extractorId, null);

    public static void DocumentFailed(
        ILogger logger, string documentId, EdgeErrorCode code, string reason, Exception? error) =>
        DocumentFailedCore(logger, documentId, (int)code, reason, error);

    public static void DocumentUnsupported(ILogger logger, string documentId, string mediaType) =>
        DocumentUnsupportedCore(logger, documentId, mediaType, null);

    public static void ExtractorSelected(ILogger logger, string documentId, string extractorId, int version) =>
        ExtractorSelectedCore(logger, documentId, extractorId, version, null);

    public static void StaleDocumentsPruned(ILogger logger, string sourceId, int count) =>
        StaleDocumentsPrunedCore(logger, sourceId, count, null);

    public static void RecipeChanged(ILogger logger, string storedHash, string currentHash) =>
        RecipeChangedCore(logger, storedHash, currentHash, null);

    public static void OrdinalsRepaired(ILogger logger, string documentId, int count) =>
        OrdinalsRepairedCore(logger, documentId, count, null);

    public static void EmbedBatchShrunk(ILogger logger, int from, int to, string reason) =>
        EmbedBatchShrunkCore(logger, from, to, reason, null);

    public static void ThrottleAdjusted(ILogger logger, int from, int to, string reason) =>
        ThrottleAdjustedCore(logger, from, to, reason, null);

    public static void ThrottlePaused(ILogger logger, string reason) =>
        ThrottlePausedCore(logger, reason, null);

    public static void StateCommitted(ILogger logger, string documentId, string status) =>
        StateCommittedCore(logger, documentId, status, null);

    public static void GraceWindowMissed(ILogger logger, string phase) =>
        GraceWindowMissedCore(logger, phase, null);

    private static EventId Id(int id, string name) => new(id, name);
}
