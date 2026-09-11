using Microsoft.Extensions.Logging;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Every log site the turn pipeline and the decode loop have, through <c>LoggerMessage.Define</c>
/// because CA1848 is an error repo-wide.
/// </summary>
/// <remarks>
/// <b>Spec section 14.4's privacy rule lives in the shape of this class (plan adjustment 27).</b>
/// This assembly's only code that holds a whole prompt and a whole completion in a local variable
/// is the turn pipeline and the decode loop, which makes this the only place the contract can be
/// broken: the formatted prompt, the reduced message list, the cached conversation text, the
/// stop-sequence buffer and every decoded token are declared at <see cref="LogLevel.Trace"/> and
/// never above, because a RAG prompt contains the user's private corpus. 930-937 carry counts,
/// reasons, ids and durations only. <c>LoggingPrivacyTests</c> is the assertion.
/// </remarks>
internal static class ChatTurnLog
{
    private static readonly Action<ILogger, string, string, int, int, string, Exception?> TurnStartedCallback =
        LoggerMessage.Define<string, string, int, int, string>(
            LogLevel.Debug,
            new EventId(EdgeChatEventIds.TurnStarted, nameof(EdgeChatEventIds.TurnStarted)),
            "Turn {ResponseId} started on {ModelId}: {PromptTokens} prompt token(s) in the sequence, " +
            "{PromptTokensAppended} appended this turn, generator {GeneratorOutcome}.");

    private static readonly Action<ILogger, string, int, double, double, double, Exception?> TurnCompletedCallback =
        LoggerMessage.Define<string, int, double, double, double>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.TurnCompleted, nameof(EdgeChatEventIds.TurnCompleted)),
            "Turn ended {StopReason}: {GeneratedTokens} token(s) in {DurationMs:0.#} ms, " +
            "{TokensPerSecond:0.##} tok/s, first token after {TtftMs:0.#} ms.");

    private static readonly Action<ILogger, int, Exception?> TurnQueuedCallback =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(EdgeChatEventIds.TurnQueued, nameof(EdgeChatEventIds.TurnQueued)),
            "Turn queued: {QueueDepth} turn(s) are waiting for the model gate.");

    private static readonly Action<ILogger, string, Exception?> TurnRejectedCallback =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(EdgeChatEventIds.TurnRejected, nameof(EdgeChatEventIds.TurnRejected)),
            "Turn refused: {Reason}");

    private static readonly Action<ILogger, string, int, Exception?> TurnTerminatedCallback =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(EdgeChatEventIds.TurnTerminated, nameof(EdgeChatEventIds.TurnTerminated)),
            "Turn terminated ({StopReason}) after {GeneratedTokens} token(s); the partial answer was delivered.");

    private static readonly Action<ILogger, int, int, int, Exception?> HistoryReducedCallback =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.HistoryReduced, nameof(EdgeChatEventIds.HistoryReduced)),
            "History reduced: {MessagesDropped} of {MessageCount} message(s) evicted to fit {BudgetTokens} token(s).");

    private static readonly Action<ILogger, double, EdgeThermalState, float?, Exception?> ThermalThrottledCallback =
        LoggerMessage.Define<double, EdgeThermalState, float?>(
            LogLevel.Warning,
            new EventId(EdgeChatEventIds.ThermalThrottled, nameof(EdgeChatEventIds.ThermalThrottled)),
            "Decode paced to {TokensPerSecond} tok/s: thermal state {Thermal}, headroom {Headroom}.");

    private static readonly Action<ILogger, EdgeThermalState, int, Exception?> ThermalAbortedCallback =
        LoggerMessage.Define<EdgeThermalState, int>(
            LogLevel.Warning,
            new EventId(EdgeChatEventIds.ThermalAborted, nameof(EdgeChatEventIds.ThermalAborted)),
            "Turn aborted mid-decode: thermal state {Thermal} after {GeneratedTokens} token(s).");

    // ---- Trace-only: text the user owns ------------------------------------------------------------

    private static readonly Action<ILogger, string, Exception?> FormattedPromptCallback =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeChatEventIds.TurnStarted, nameof(EdgeChatEventIds.TurnStarted)),
            "Formatted prompt: {Prompt}");

    private static readonly Action<ILogger, string, Exception?> ReducedMessagesCallback =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeChatEventIds.HistoryReduced, nameof(EdgeChatEventIds.HistoryReduced)),
            "Reduced messages: {Messages}");

    private static readonly Action<ILogger, string, Exception?> CachedTextCallback =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeChatEventIds.TurnStarted, nameof(EdgeChatEventIds.TurnStarted)),
            "Conversation cache text: {CachedText}");

    private static readonly Action<ILogger, string, string, Exception?> TokenDecodedCallback =
        LoggerMessage.Define<string, string>(
            LogLevel.Trace,
            new EventId(EdgeChatEventIds.TurnCompleted, nameof(EdgeChatEventIds.TurnCompleted)),
            "Decoded token {Text}; stop buffer holds {StopBuffer}");

    public static void TurnStarted(
        ILogger logger,
        string responseId,
        string modelId,
        int promptTokens,
        int promptTokensAppended,
        string generatorOutcome)
        => TurnStartedCallback(logger, responseId, modelId, promptTokens, promptTokensAppended, generatorOutcome, null);

    public static void TurnCompleted(ILogger logger, ChatTurnStatus status)
        => TurnCompletedCallback(
            logger,
            status.StopReason.ToString(),
            status.GeneratedTokens,
            status.Duration.TotalMilliseconds,
            status.TokensPerSecond,
            status.TimeToFirstToken.TotalMilliseconds,
            null);

    public static void TurnQueued(ILogger logger, int queueDepth) => TurnQueuedCallback(logger, queueDepth, null);

    public static void TurnRejected(ILogger logger, string reason) => TurnRejectedCallback(logger, reason, null);

    public static void TurnTerminated(ILogger logger, EdgeChatStopReason reason, int generated)
        => TurnTerminatedCallback(logger, reason.ToString(), generated, null);

    public static void HistoryReduced(ILogger logger, int dropped, int total, int budget)
        => HistoryReducedCallback(logger, dropped, total, budget, null);

    public static void ThermalThrottled(ILogger logger, double rate, EdgeThermalState thermal, float? headroom)
        => ThermalThrottledCallback(logger, rate, thermal, headroom, null);

    public static void ThermalAborted(ILogger logger, EdgeThermalState thermal, int generated)
        => ThermalAbortedCallback(logger, thermal, generated, null);

    public static void FormattedPrompt(ILogger logger, string prompt)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            FormattedPromptCallback(logger, prompt, null);
        }
    }

    public static void ReducedMessages(ILogger logger, string messages)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            ReducedMessagesCallback(logger, messages, null);
        }
    }

    public static void CachedText(ILogger logger, string cachedText)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            CachedTextCallback(logger, cachedText, null);
        }
    }

    public static void TokenDecoded(ILogger logger, string text, string stopBuffer)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            TokenDecodedCallback(logger, text, stopBuffer, null);
        }
    }
}
