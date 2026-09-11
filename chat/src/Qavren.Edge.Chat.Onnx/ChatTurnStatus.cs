using Microsoft.Extensions.AI;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat;

/// <summary>Why a turn ended.</summary>
/// <remarks>
/// MEAI's <c>ChatFinishReason</c> cannot express "the OS suspended us" or "we ran out of memory
/// mid-turn", and a turn that was cut short must never be presented as one that finished. Both are
/// emitted: the nearest <c>ChatFinishReason</c> for the ecosystem, and this for the truth.
/// </remarks>
public enum EdgeChatStopReason
{
    /// <summary>The model emitted its own end-of-sequence token.</summary>
    Completed,

    /// <summary>The per-turn output cap bound.</summary>
    MaxOutputTokens,

    /// <summary>An effective stop sequence matched the accumulated text.</summary>
    StopSequence,

    /// <summary>The caller cancelled.</summary>
    Cancelled,

    /// <summary>The OS backgrounded the app mid-decode.</summary>
    Suspended,

    /// <summary>Critical memory pressure arrived mid-decode.</summary>
    MemoryPressure,

    /// <summary>Thermal state reached <c>ChatThermalOptions.AbortAt</c> mid-decode.</summary>
    Thermal,

    /// <summary>The native layer threw; the partial text already streamed stays streamed.</summary>
    Error,
}

/// <summary>
/// One turn's honest record, carried on the final <c>ChatResponseUpdate</c> and on the
/// <c>ChatResponse</c> under <see cref="EdgeChatProperties.TurnStatus"/>. One strongly-typed record
/// under one key, rather than seven loose string keys.
/// </summary>
/// <param name="StopReason">Why the turn ended.</param>
/// <param name="ModelId">The model that produced it.</param>
/// <param name="ConversationId">
/// The effective id for this response - the caller's if they supplied one, otherwise the one this
/// client minted. Also on every <c>ChatResponseUpdate.ConversationId</c>.
/// </param>
/// <param name="PromptTokens">
/// Total sequence length the model conditioned on, after any conversation-cache append. Equal to
/// <c>UsageDetails.InputTokenCount</c> on the same response.
/// </param>
/// <param name="PromptTokensAppended">
/// Tokens actually encoded and appended this turn: equal to <paramref name="PromptTokens"/> on a
/// fresh generator, and the delta alone on a conversation-cache hit.
/// </param>
/// <param name="GeneratedTokens">Tokens this turn produced.</param>
/// <param name="ContextTokens">The context the budget allowed.</param>
/// <param name="MessagesDropped">How many messages history reduction evicted.</param>
/// <param name="TimeToFirstToken">Prefill plus the first decode step.</param>
/// <param name="Duration">The whole turn.</param>
/// <param name="TokensPerSecond">Decode throughput, excluding prefill.</param>
/// <param name="Thermal">
/// The thermal state at the end of the turn, published verbatim - <c>Unknown</c> is never
/// normalised to <c>Nominal</c>.
/// </param>
/// <param name="ThermalThrottled">Whether the decode was paced at any point.</param>
public sealed record ChatTurnStatus(
    EdgeChatStopReason StopReason,
    string ModelId,
    string? ConversationId,
    int PromptTokens,
    int PromptTokensAppended,
    int GeneratedTokens,
    int ContextTokens,
    int MessagesDropped,
    TimeSpan TimeToFirstToken,
    TimeSpan Duration,
    double TokensPerSecond,
    EdgeThermalState Thermal,
    bool ThermalThrottled);

/// <summary>Reads sub-project 4's turn record off an MEAI response without typing a string literal.</summary>
public static class EdgeChat
{
    /// <summary>Reads the turn status off an aggregated response.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The status, or null when the response did not come from this client.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    public static ChatTurnStatus? GetTurnStatus(this ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return Read(response.AdditionalProperties);
    }

    /// <summary>Reads the turn status off a streaming update.</summary>
    /// <param name="update">The update; only the final one carries it.</param>
    /// <returns>The status, or null when this update does not carry one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="update"/> is null.</exception>
    public static ChatTurnStatus? GetTurnStatus(this ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        return Read(update.AdditionalProperties);
    }

    private static ChatTurnStatus? Read(AdditionalPropertiesDictionary? properties)
        => properties is not null
            && properties.TryGetValue(EdgeChatProperties.TurnStatus, out var value)
                ? value as ChatTurnStatus
                : null;
}

/// <summary>What one client has done since it was resolved.</summary>
/// <param name="Turns">Turns that ran.</param>
/// <param name="RejectedTurns">Turns refused by the queue, the gate or a pre-flight.</param>
/// <param name="TokensGenerated">Tokens produced across every turn.</param>
/// <param name="TokensPerSecondP50">Median decode throughput, or null before the first turn.</param>
/// <param name="TokensPerSecondP95">95th-percentile decode throughput, or null before the first turn.</param>
/// <param name="LastTokensPerSecond">The most recent turn's decode throughput.</param>
/// <param name="LastTimeToFirstToken">The most recent turn's prefill latency.</param>
/// <param name="ProcessWorkingSetBytes">The process working set, or null when the platform will not say.</param>
/// <param name="PeakWorkingSetBytes">The peak working set, or null when the platform will not say.</param>
/// <param name="ThermalThrottleEvents">Turns that were paced.</param>
/// <param name="ThermalAbortEvents">Turns that were aborted on thermal state.</param>
/// <param name="UnloadEvents">Times the model was dropped.</param>
/// <param name="TerminationEvents">Turns cut short by the OS, memory pressure or thermal state.</param>
/// <param name="HistoryReductions">Turns whose history was reduced.</param>
/// <param name="LastStopReason">Why the most recent turn ended.</param>
public sealed record ChatClientStatistics(
    int Turns,
    int RejectedTurns,
    long TokensGenerated,
    double? TokensPerSecondP50,
    double? TokensPerSecondP95,
    double? LastTokensPerSecond,
    TimeSpan? LastTimeToFirstToken,
    long? ProcessWorkingSetBytes,
    long? PeakWorkingSetBytes,
    int ThermalThrottleEvents,
    int ThermalAbortEvents,
    int UnloadEvents,
    int TerminationEvents,
    int HistoryReductions,
    EdgeChatStopReason? LastStopReason);
