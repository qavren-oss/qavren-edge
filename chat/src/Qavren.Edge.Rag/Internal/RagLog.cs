using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Rag.Internal;

/// <summary>
/// Every log site in <c>Qavren.Edge.Rag</c>, through <c>LoggerMessage.Define</c> because this
/// repo's <c>TreatWarningsAsErrors</c> plus <c>latest-recommended</c> makes CA1848 an error.
/// </summary>
/// <remarks>
/// <b>Spec 14.4's privacy rule is enforced by the shape of this class, not by convention.</b> Every
/// delegate that carries a question, a retrieved chunk, an assembled prompt or a completion is
/// declared at <see cref="LogLevel.Trace"/> and nowhere else, because a RAG prompt contains the
/// user's private corpus. The <c>Debug</c>, <c>Information</c> and <c>Warning</c> sites carry
/// counts, ordinals, retriever names, elapsed milliseconds and error codes - never text.
/// <c>LoggingPrivacyTests</c> is the assertion.
/// </remarks>
internal static class RagLog
{
    private static readonly Action<ILogger, string, int, double, Exception?> RetrievedCallback =
        LoggerMessage.Define<string, int, double>(
            LogLevel.Debug,
            new EventId(EdgeRagEventIds.Retrieved, nameof(EdgeRagEventIds.Retrieved)),
            "Retriever '{RetrieverName}' returned {SourceCount} source(s) in {ElapsedMs:0.###} ms.");

    private static readonly Action<ILogger, string, Exception?> RetrievalQueryCallback =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeRagEventIds.Retrieved, nameof(EdgeRagEventIds.Retrieved)),
            "Retrieval query: {Query}");

    private static readonly Action<ILogger, int, int, int, Exception?> ContextAssembledCallback =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Debug,
            new EventId(EdgeRagEventIds.ContextAssembled, nameof(EdgeRagEventIds.ContextAssembled)),
            "Assembled a context block of {IncludedCount} of {RetrievedCount} source(s), " +
            "about {ContextTokens} token(s).");

    private static readonly Action<ILogger, string, Exception?> ContextBlockCallback =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeRagEventIds.ContextAssembled, nameof(EdgeRagEventIds.ContextAssembled)),
            "Context block: {Block}");

    private static readonly Action<ILogger, string, int, Exception?> RetrievalFailedCallback =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(EdgeRagEventIds.RetrievalFailed, nameof(EdgeRagEventIds.RetrievalFailed)),
            "Retriever '{RetrieverName}' threw; the turn continues ungrounded " +
            "(ContinueOnRetrievalFailure). Failure {FailureCount} for this client.");

    private static readonly Action<ILogger, string, Exception?> NoContextCallback =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(EdgeRagEventIds.NoContext, nameof(EdgeRagEventIds.NoContext)),
            "Retriever '{RetrieverName}' returned nothing; answering without a model call.");

    private static readonly Action<ILogger, int, int, Exception?> CitationsAttachedCallback =
        LoggerMessage.Define<int, int>(
            LogLevel.Debug,
            new EventId(EdgeRagEventIds.CitationsAttached, nameof(EdgeRagEventIds.CitationsAttached)),
            "Attached {CitationCount} citation(s) over {SourceCount} source(s) to the final update.");

    private static readonly Action<ILogger, string, Exception?> AnswerCallback =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeRagEventIds.CitationsAttached, nameof(EdgeRagEventIds.CitationsAttached)),
            "Answer: {Answer}");

    private static readonly Action<ILogger, int, Exception?> CitationUnresolvedCallback =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(EdgeRagEventIds.CitationUnresolved, nameof(EdgeRagEventIds.CitationUnresolved)),
            "{UnresolvedCount} bracketed marker(s) matched no source and were left as plain text.");

    private static readonly Action<ILogger, int, Exception?> ExtractiveAnswerCallback =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(EdgeRagEventIds.ExtractiveAnswer, nameof(EdgeRagEventIds.ExtractiveAnswer)),
            "The extractive floor answered from {SourceCount} source(s) with no model.");

    private static readonly Action<ILogger, string, Exception?> ExtractiveAnswerTextCallback =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeRagEventIds.ExtractiveAnswer, nameof(EdgeRagEventIds.ExtractiveAnswer)),
            "Extractive answer: {Answer}");

    internal static void Retrieved(ILogger logger, string retrieverName, int sourceCount, double elapsedMs) =>
        RetrievedCallback(logger, retrieverName, sourceCount, elapsedMs, null);

    internal static void RetrievalQuery(ILogger logger, string query) =>
        RetrievalQueryCallback(logger, query, null);

    internal static void ContextAssembled(ILogger logger, int included, int retrieved, int contextTokens) =>
        ContextAssembledCallback(logger, included, retrieved, contextTokens, null);

    internal static void ContextBlock(ILogger logger, string block) =>
        ContextBlockCallback(logger, block, null);

    internal static void RetrievalFailed(ILogger logger, string retrieverName, long failureCount, Exception error) =>
        RetrievalFailedCallback(logger, retrieverName, (int)failureCount, error);

    internal static void NoContext(ILogger logger, string retrieverName) =>
        NoContextCallback(logger, retrieverName, null);

    internal static void CitationsAttached(ILogger logger, int citationCount, int sourceCount) =>
        CitationsAttachedCallback(logger, citationCount, sourceCount, null);

    internal static void Answer(ILogger logger, string answer) =>
        AnswerCallback(logger, answer, null);

    internal static void CitationUnresolved(ILogger logger, int unresolvedCount) =>
        CitationUnresolvedCallback(logger, unresolvedCount, null);

    internal static void ExtractiveAnswer(ILogger logger, int sourceCount) =>
        ExtractiveAnswerCallback(logger, sourceCount, null);

    internal static void ExtractiveAnswerText(ILogger logger, string answer) =>
        ExtractiveAnswerTextCallback(logger, answer, null);
}
