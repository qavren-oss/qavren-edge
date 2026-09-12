using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Internal;

/// <summary>Everything the producer body needs, handed over once so the loop has no hidden inputs.</summary>
internal sealed class ChatDecodeContext
{
    public required IChatGenerator Generator { get; init; }

    public required IChatTokenStream Stream { get; init; }

    public required StopSequenceMatcher StopMatcher { get; init; }

    public required ChannelWriter<ChatResponseUpdate> Writer { get; init; }

    public required IChatTurnHost Host { get; init; }

    public required IEdgeResourceMonitor Monitor { get; init; }

    public required TimeProvider TimeProvider { get; init; }

    public required ILogger Logger { get; init; }

    public required ChatThermalOptions Thermal { get; init; }

    /// <summary>The managed per-turn output cap.</summary>
    public required int MaxOutputTokens { get; init; }

    /// <summary>The <c>max_length</c> the generator was built with, which is the KV memory cap.</summary>
    public required int MaxLength { get; init; }

    public required int ResolvedContextTokens { get; init; }

    public required int PromptTokens { get; init; }

    public required int PromptTokensAppended { get; init; }

    public required int MessagesDropped { get; init; }

    public required string ModelId { get; init; }

    public required string? ConversationId { get; init; }

    public required string ResponseId { get; init; }

    public required string MessageId { get; init; }

    public required IReadOnlyList<string> UnhonouredOptions { get; init; }

    /// <summary>The thermal reading the pre-flight took, so the first sample window starts there.</summary>
    public required EdgeResourceSnapshot InitialSnapshot { get; init; }

    /// <summary>The timestamp the turn began at, so <c>Duration</c> includes prefill.</summary>
    public required long TurnStartedAt { get; init; }

    /// <summary>The caller's token.</summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>Cancelled when the consumer stops reading, so a blocked write never deadlocks.</summary>
    public required CancellationToken ConsumerGone { get; init; }
}

/// <summary>What the producer body reports back to the turn.</summary>
/// <param name="Status">The turn record the final update carried.</param>
/// <param name="DecodedText">Every decoded fragment, raw, for the conversation cache.</param>
/// <param name="Cancelled">Whether the caller's token cancelled the turn.</param>
/// <param name="Failure">What the native layer threw, when it did.</param>
internal sealed record ChatDecodeResult(
    ChatTurnStatus Status,
    string DecodedText,
    bool Cancelled,
    Exception? Failure);

/// <summary>
/// The producer body: runs on one <c>Task.Run</c>, writes a bounded channel, and <b>never
/// yields</b>. A <c>yield return</c> here would put <c>GenerateNextToken()</c> back on the
/// consumer's thread and undo the whole arrangement.
/// </summary>
/// <remarks>
/// Three details are the reason this loop exists rather than upstream's: stop sequences are matched
/// over a rolling decoded-character buffer; thermal is sampled mid-decode at the monitor's own
/// cache boundary; and throttling paces rather than stops. The writer is always completed in a
/// <c>finally</c>, on success, fault and cancel, and the final update - carrying the real counts
/// and the honest stop reason - is written before any exception is reported.
/// </remarks>
internal static class ChatDecodeLoop
{
    /// <summary>Runs the loop to completion. Never throws: every outcome is in the result.</summary>
    /// <param name="context">The turn.</param>
    /// <returns>The result.</returns>
    public static async Task<ChatDecodeResult> RunAsync(ChatDecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var generator = context.Generator;
        var writer = context.Writer;
        var ct = context.CancellationToken;
        var thermal = context.Thermal;
        var logger = context.Logger;

        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, context.ConsumerGone);
        var writeToken = writeCancellation.Token;

        var stop = EdgeChatStopReason.Completed;
        var generated = 0;
        var decoded = new StringBuilder();
        var timeToFirstToken = TimeSpan.Zero;
        var decodeStartedAt = Stopwatch.GetTimestamp();
        var firstTokenAt = 0L;
        var throttled = false;
        var everThrottled = false;
        var snapshot = context.InitialSnapshot;
        var lastSample = context.TimeProvider.GetUtcNow();
        var nextPacedToken = context.TimeProvider.GetUtcNow();
        var cancelled = false;
        Exception? failure = null;

        // The pre-flight already refused an abort-level reading; what it did not do is decide the
        // pace, so the reading it took is evaluated for throttling before the first token.
        throttled = ShouldThrottle(snapshot, thermal);
        if (throttled)
        {
            everThrottled = true;
            ChatTurnLog.ThermalThrottled(logger, thermal.ThrottledTokensPerSecond, snapshot.Thermal, snapshot.ThermalHeadroom);
        }

        try
        {
            while (!generator.IsDone())
            {
                ct.ThrowIfCancellationRequested();

                if (context.Host.TerminationRequested)
                {
                    stop = context.Host.SuspendRequested
                        ? EdgeChatStopReason.Suspended
                        : EdgeChatStopReason.MemoryPressure;
                    ChatTurnLog.TurnTerminated(logger, stop, generated);
                    break;
                }

                var now = context.TimeProvider.GetUtcNow();
                if (now - lastSample >= thermal.SampleInterval)
                {
                    snapshot = context.Monitor.Read();
                    lastSample = now;

                    // Unknown is 0, BELOW Nominal, so `>=` alone would read "nothing usable" as
                    // "fine". The guard is explicit, not implied by the enum's numbering.
                    var known = snapshot.Thermal != EdgeThermalState.Unknown;

                    if (known && snapshot.Thermal >= thermal.AbortAt)
                    {
                        TryTerminate(generator);
                        ChatTurnLog.ThermalAborted(logger, snapshot.Thermal, generated);
                        stop = EdgeChatStopReason.Thermal;
                        break;
                    }

                    throttled = ShouldThrottle(snapshot, thermal);

                    if (throttled && !everThrottled)
                    {
                        everThrottled = true;
                        ChatTurnLog.ThermalThrottled(
                            logger,
                            thermal.ThrottledTokensPerSecond,
                            snapshot.Thermal,
                            snapshot.ThermalHeadroom);
                    }
                }

                if (throttled)
                {
                    nextPacedToken = await PaceAsync(context, nextPacedToken, ct).ConfigureAwait(false);
                }

                // The blocking native call, off the caller's thread.
                generator.GenerateNextToken();

                // A cancel or a termination that landed INSIDE that call makes the token it
                // returned with untrustworthy - terminate_session aborts mid-step - so neither is
                // counted or decoded; the turn stops at this boundary with the honest reason.
                ct.ThrowIfCancellationRequested();
                if (context.Host.TerminationRequested)
                {
                    stop = context.Host.SuspendRequested
                        ? EdgeChatStopReason.Suspended
                        : EdgeChatStopReason.MemoryPressure;
                    ChatTurnLog.TurnTerminated(logger, stop, generated);
                    break;
                }

                generated++;

                var text = context.Stream.Decode(generator.LastToken());
                decoded.Append(text);

                if (firstTokenAt == 0)
                {
                    firstTokenAt = Stopwatch.GetTimestamp();
                    timeToFirstToken = Stopwatch.GetElapsedTime(context.TurnStartedAt, firstTokenAt);
                }

                var emit = context.StopMatcher.Push(text, out var matched);
                ChatTurnLog.TokenDecoded(logger, text, context.StopMatcher.Pending);

                if (emit.Length > 0)
                {
                    await writer.WriteAsync(Update(context, emit), writeToken).ConfigureAwait(false);
                }

                if (matched)
                {
                    stop = EdgeChatStopReason.StopSequence;
                    break;
                }

                // The per-turn OUTPUT cap. max_length is the KV MEMORY cap and, on a cached
                // generator, cannot be changed after construction - spec section 10(e).
                if (generated >= context.MaxOutputTokens)
                {
                    var rest = context.StopMatcher.Finish(out var matchedAtEnd);
                    if (rest.Length > 0)
                    {
                        await writer.WriteAsync(Update(context, rest), writeToken).ConfigureAwait(false);
                    }

                    stop = matchedAtEnd ? EdgeChatStopReason.StopSequence : EdgeChatStopReason.MaxOutputTokens;
                    break;
                }
            }

            if (stop == EdgeChatStopReason.Completed)
            {
                // The loop ended because the generator said so: end-of-sequence, or max_length.
                var rest = context.StopMatcher.Finish(out var matchedAtEnd);
                if (rest.Length > 0)
                {
                    await writer.WriteAsync(Update(context, rest), writeToken).ConfigureAwait(false);
                }

                if (matchedAtEnd)
                {
                    stop = EdgeChatStopReason.StopSequence;
                }
                else if (generator.TokenCount() >= (ulong)context.MaxLength)
                {
                    stop = EdgeChatStopReason.MaxOutputTokens;
                }
            }
        }
        catch (OnnxRuntimeGenAIException) when (ct.IsCancellationRequested)
        {
            // The caller's cancel registration set terminate_session while IsDone() or
            // GenerateNextToken() was inside native code, and GenAI 0.15.2 reports that as a throw
            // ("Exiting due to terminate flag being set to true") rather than as a returned step.
            // That is the cancel the caller asked for, not a native fault: same outcome as below.
            cancelled = true;
            stop = EdgeChatStopReason.Cancelled;
        }
        catch (OnnxRuntimeGenAIException) when (context.Host.TerminationRequested)
        {
            // TerminateActiveGeneration() from another thread - MemoryPressure(Critical), Sleeping
            // or a caller - landed inside the native call instead of between two tokens. Spec
            // section 15.3: the stream COMPLETES with StopReason = MemoryPressure / Suspended and
            // FinishReason.Stop; it is not a 7104.
            stop = context.Host.SuspendRequested
                ? EdgeChatStopReason.Suspended
                : EdgeChatStopReason.MemoryPressure;
            ChatTurnLog.TurnTerminated(logger, stop, generated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Never converted; reported honestly on the final update, then rethrown by the turn.
            cancelled = true;
            stop = EdgeChatStopReason.Cancelled;
            TryTerminate(generator);
        }
        catch (OperationCanceledException) when (context.ConsumerGone.IsCancellationRequested)
        {
            // The consumer stopped reading. Nobody is left to receive the final update, but the
            // generator is still stopped and the counts are still recorded.
            stop = EdgeChatStopReason.Cancelled;
            TryTerminate(generator);
        }
#pragma warning disable CA1031 // Every native failure is 7104, and the partial answer stays streamed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            stop = EdgeChatStopReason.Error;
            failure = ex;
        }

        var duration = Stopwatch.GetElapsedTime(context.TurnStartedAt);
        var decodeSeconds = firstTokenAt == 0
            ? 0
            : Stopwatch.GetElapsedTime(decodeStartedAt).TotalSeconds;
        var tokensPerSecond = generated > 0 && decodeSeconds > 0 ? generated / decodeSeconds : 0;

        var status = new ChatTurnStatus(
            stop,
            context.ModelId,
            context.ConversationId,
            context.PromptTokens,
            context.PromptTokensAppended,
            generated,
            context.ResolvedContextTokens,
            context.MessagesDropped,
            timeToFirstToken,
            duration,
            tokensPerSecond,
            snapshot.Thermal,
            everThrottled);

        try
        {
            // Step (g): exactly one final, metadata-only update with the same MessageId - written
            // with the consumer's token only, so a cancelled turn still delivers its counts.
            await writer.WriteAsync(FinalUpdate(context, status), context.ConsumerGone).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The consumer is gone; there is nobody to deliver it to.
        }
        catch (ChannelClosedException)
        {
            // Same race, the channel's spelling of it.
        }
        finally
        {
            // Step (h): ALWAYS, on success, fault and cancel.
            writer.TryComplete();
        }

        return new ChatDecodeResult(status, decoded.ToString(), cancelled, failure);
    }

    /// <summary>
    /// <c>thermal != Unknown &amp;&amp; thermal &gt;= ThrottleAt</c>, or the headroom at its
    /// threshold. Written out because <c>Unknown = 0</c> sorts below <c>Nominal</c>, and because
    /// <c>NaN &gt;= x</c> is false - an unreadable headroom never throttles.
    /// </summary>
    private static bool ShouldThrottle(EdgeResourceSnapshot snapshot, ChatThermalOptions thermal)
    {
        var known = snapshot.Thermal != EdgeThermalState.Unknown;
        var byState = known && snapshot.Thermal >= thermal.ThrottleAt;
        var byHeadroom = thermal.ThrottleHeadroom is { } threshold
            && snapshot.ThermalHeadroom is { } headroom
            && headroom >= threshold;

        return byState || byHeadroom;
    }

    private static async Task<DateTimeOffset> PaceAsync(
        ChatDecodeContext context,
        DateTimeOffset nextAllowed,
        CancellationToken ct)
    {
        var rate = context.Thermal.ThrottledTokensPerSecond;
        if (rate <= 0 || double.IsNaN(rate) || double.IsInfinity(rate))
        {
            return nextAllowed;
        }

        var interval = TimeSpan.FromSeconds(1.0 / rate);
        var now = context.TimeProvider.GetUtcNow();

        if (nextAllowed > now)
        {
            await Task.Delay(nextAllowed - now, context.TimeProvider, ct).ConfigureAwait(false);
            return nextAllowed + interval;
        }

        return now + interval;
    }

    private static void TryTerminate(IChatGenerator generator)
    {
        try
        {
            ChatTurnPipeline.Terminate(generator);
        }
#pragma warning disable CA1031 // A generator that has already finished cannot be terminated, and that is not a failure.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static ChatResponseUpdate Update(ChatDecodeContext context, string text) =>
        new(ChatRole.Assistant, text)
        {
            MessageId = context.MessageId,
            ResponseId = context.ResponseId,
            ConversationId = context.ConversationId,
            ModelId = context.ModelId,
        };

    private static ChatResponseUpdate FinalUpdate(ChatDecodeContext context, ChatTurnStatus status)
    {
        var usage = new UsageDetails
        {
            InputTokenCount = status.PromptTokens,
            OutputTokenCount = status.GeneratedTokens,
            TotalTokenCount = status.PromptTokens + status.GeneratedTokens,
        };

        var update = new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])
        {
            MessageId = context.MessageId,
            ResponseId = context.ResponseId,
            ConversationId = context.ConversationId,
            ModelId = context.ModelId,
            FinishReason = status.StopReason == EdgeChatStopReason.MaxOutputTokens
                ? ChatFinishReason.Length
                : ChatFinishReason.Stop,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [EdgeChatProperties.TurnStatus] = status,
            },
        };

        if (context.UnhonouredOptions.Count > 0)
        {
            update.AdditionalProperties[EdgeChatProperties.UnhonouredOptions] = context.UnhonouredOptions;
        }

        return update;
    }
}
