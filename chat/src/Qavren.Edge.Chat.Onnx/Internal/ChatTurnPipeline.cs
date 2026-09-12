using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Internal;

/// <summary>The four generation knobs after spec section 6.6's precedence rule has been applied.</summary>
/// <param name="MaxOutputTokens">The per-turn output cap.</param>
/// <param name="Temperature">The sampling temperature.</param>
/// <param name="TopP">The nucleus cut-off.</param>
/// <param name="TopK">The top-k cut-off.</param>
/// <param name="Seed">The per-call seed, or null.</param>
/// <param name="RepetitionPenalty">The per-call <c>PresencePenalty</c>, or null.</param>
internal readonly record struct ChatTurnKnobs(
    int MaxOutputTokens,
    float Temperature,
    float TopP,
    int TopK,
    long? Seed,
    float? RepetitionPenalty);

/// <summary>
/// Spec section 10's steps (a) to (e), as pure functions over the options - every one a tier-1
/// unit test with no natives. The client orchestrates; this is what it orchestrates.
/// </summary>
internal static class ChatTurnPipeline
{
    /// <summary>The <c>ChatClientMetadata.ProviderName</c>, which is also OpenTelemetry's <c>gen_ai.system</c>.</summary>
    public const string ProviderName = "onnxruntime-genai";

    private const string TerminateSessionKey = "terminate_session";

    /// <summary>The runtime option that aborts a generator inside native code.</summary>
    public static void Terminate(IChatGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        generator.SetRuntimeOption(TerminateSessionKey, "1");
    }

    /// <summary>
    /// The inverse: clears <c>terminate_session</c> before a turn starts. GenAI 0.15.2 accepts
    /// <c>"0"</c> and resets the flag (<c>State::SetRunOption</c>), and
    /// <c>TerminationLatchedGenerator</c> unlatches on the same value. Called for every turn, so a
    /// generator reused from the conversation cache can never carry a terminate that raced the end
    /// of its previous turn into the next one - where it would answer "done" before the first token.
    /// </summary>
    public static void Resume(IChatGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        generator.SetRuntimeOption(TerminateSessionKey, "0");
    }

    // ---- (a) Refuse early, by name ---------------------------------------------------------------------

    /// <summary>
    /// Refuses what this client cannot honour, names what it will not honour, and lets the rest
    /// through. <c>ChatResponseFormat.Text</c> and a null <c>ResponseFormat</c> are untouched:
    /// refusing a caller for requesting the default behaviour would be a bug.
    /// </summary>
    /// <param name="messages">The conversation.</param>
    /// <param name="options">The per-call options, or null.</param>
    /// <param name="edge">This registration's options.</param>
    /// <returns>The <c>ChatOptions</c> members set on the request that this client does not honour.</returns>
    /// <exception cref="EdgeChatException">7107, 7103 or 7108.</exception>
    public static IReadOnlyList<string> Refuse(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        EdgeChatOptions edge)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(edge);

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is not TextContent)
                {
                    throw OptionUnsupported(
                        $"a message of role '{message.Role}' carries {content.GetType().Name}, and this client " +
                        "generates text from text only",
                        "Send TextContent only; images, audio, function calls and results have no on-device " +
                        "path in v1 (ADR 0012).");
                }
            }
        }

        if (options is null)
        {
            return [];
        }

        if (options.Tools is { Count: > 0 })
        {
            throw ToolCallingUnsupported($"ChatOptions.Tools holds {options.Tools.Count} tool(s)");
        }

        if (options.ToolMode is RequiredChatToolMode required)
        {
            var which = required.RequiredFunctionName is { } name
                ? $"ChatToolMode.RequireSpecific('{name}')"
                : "ChatToolMode.RequireAny";
            throw ToolCallingUnsupported($"{which} demands a tool call this client can never emit");
        }

        if (options.ResponseFormat is ChatResponseFormatJson && edge.Guidance == EdgeGuidancePolicy.Disabled)
        {
            throw new EdgeChatException(
                EdgeErrorCode.ChatGuidanceUnavailable,
                "A ChatResponseFormatJson was requested and EdgeChatOptions.Guidance is Disabled. The " +
                "shipped mobile natives are built without USE_GUIDANCE, and on such a build a " +
                "constrained-decoding request is silently ignored - so this client refuses it by " +
                "name rather than returning unconstrained text as if it were constrained.")
            {
                PresetId = edge.Preset?.Id,
                Remediation =
                    "Set EdgeChatOptions.Guidance to PreferNative or RequireNative so the guidance probe " +
                    "runs at load, or drop ResponseFormat and validate the JSON yourself.",
            };
        }

        var unhonoured = new List<string>();
        if (options.FrequencyPenalty is not null)
        {
            unhonoured.Add(nameof(ChatOptions.FrequencyPenalty));
        }

        if (options.AllowMultipleToolCalls is not null)
        {
            unhonoured.Add(nameof(ChatOptions.AllowMultipleToolCalls));
        }

        if (options.ContinuationToken is not null)
        {
            unhonoured.Add(nameof(ChatOptions.ContinuationToken));
        }

        if (options.AllowBackgroundResponses is not null)
        {
            unhonoured.Add(nameof(ChatOptions.AllowBackgroundResponses));
        }

        return unhonoured;
    }

    /// <summary>
    /// Whether the turn should ask for constrained decoding, and with what. Null when the request
    /// asked for none, or when <c>PreferNative</c> falls through unconstrained - in which case
    /// <paramref name="unhonoured"/> gains <c>ResponseFormat</c>, never a silent claim.
    /// </summary>
    /// <param name="options">The per-call options.</param>
    /// <param name="edge">The registration's options.</param>
    /// <param name="probe">The guidance probe's verdict for the loaded model.</param>
    /// <param name="unhonoured">The list to append to when guidance falls through.</param>
    /// <returns>The guidance request, or null.</returns>
    /// <exception cref="EdgeChatException">7103 under <c>RequireNative</c> when the probe did not prove enforcement.</exception>
    public static ChatGuidance? ResolveGuidance(
        ChatOptions? options,
        EdgeChatOptions edge,
        EdgeGuidanceProbeResult probe,
        List<string> unhonoured)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(unhonoured);

        if (options?.ResponseFormat is not ChatResponseFormatJson json)
        {
            return null;
        }

        if (probe == EdgeGuidanceProbeResult.Enforced)
        {
            var schema = json.Schema is { } element ? element.GetRawText() : """{"type":"object"}""";
            return new ChatGuidance("json_schema", schema);
        }

        if (edge.Guidance == EdgeGuidancePolicy.RequireNative)
        {
            throw new EdgeChatException(
                EdgeErrorCode.ChatGuidanceUnavailable,
                $"A ChatResponseFormatJson was requested under RequireNative and the guidance probe " +
                $"reported {probe}: the constraint is not proven enforced on this build and model.")
            {
                PresetId = edge.Preset?.Id,
                Remediation =
                    "Use PreferNative to fall through unconstrained (the response then lists ResponseFormat " +
                    "under UnhonouredOptions), or validate the JSON yourself.",
            };
        }

        unhonoured.Add(nameof(ChatOptions.ResponseFormat));
        return null;
    }

    // ---- (b) Gate: the pre-flight half --------------------------------------------------------------------

    /// <summary>The refusals that happen before anything is allocated and before the gate is entered.</summary>
    /// <param name="host">The model host.</param>
    /// <param name="snapshot">The monitor's current reading.</param>
    /// <param name="edge">The registration's options.</param>
    /// <exception cref="EdgeChatException">7105 or 7106.</exception>
    public static void PreFlight(IChatModelHost host, EdgeResourceSnapshot snapshot, EdgeChatOptions edge)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(edge);

        if (!host.IsAcceptingTurns)
        {
            throw Busy(
                "the host is not accepting turns: the lifecycle hub reported memory pressure and no " +
                "Resumed has cleared it yet",
                "Retry after the app is resumed.");
        }

        if (edge.Thermal.RefuseNewTurnsInLowPowerMode && snapshot.IsLowPowerMode == true)
        {
            throw Busy(
                "the device is in low-power mode and ChatThermalOptions.RefuseNewTurnsInLowPowerMode is set",
                "Retry when the device leaves low-power mode, or clear RefuseNewTurnsInLowPowerMode.");
        }

        var thermal = snapshot.Thermal;
        var known = thermal != EdgeThermalState.Unknown;

        if (known && thermal >= edge.Thermal.AbortAt)
        {
            throw ThermalAbort(thermal, $"the thermal state is {thermal}, at or above ChatThermalOptions.AbortAt ({edge.Thermal.AbortAt})");
        }

        if (!known && edge.Thermal.RefuseWhenThermalUnknown)
        {
            throw ThermalAbort(thermal, "the platform reports no usable thermal state and ChatThermalOptions.RefuseWhenThermalUnknown is set");
        }
    }

    // ---- (c) Messages and reduction ---------------------------------------------------------------------

    /// <summary>
    /// Folds <c>EdgeChatOptions.SystemPrompt</c> and <c>ChatOptions.Instructions</c> in as leading
    /// system messages, the options value first, behind any system message the caller already put
    /// at the head. Nothing is dropped, so nothing needs a winner.
    /// </summary>
    /// <param name="messages">The caller's conversation.</param>
    /// <param name="options">The per-call options, or null.</param>
    /// <param name="edge">The registration's options.</param>
    /// <returns>The conversation the reducer sees.</returns>
    public static List<ChatMessage> ComposeMessages(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        EdgeChatOptions edge)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(edge);

        var composed = new List<ChatMessage>(messages.Count + 2);
        var index = 0;

        while (index < messages.Count && messages[index].Role == ChatRole.System)
        {
            composed.Add(messages[index]);
            index++;
        }

        if (!string.IsNullOrEmpty(edge.SystemPrompt))
        {
            composed.Add(new ChatMessage(ChatRole.System, edge.SystemPrompt));
        }

        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            composed.Add(new ChatMessage(ChatRole.System, options.Instructions));
        }

        for (; index < messages.Count; index++)
        {
            composed.Add(messages[index]);
        }

        return composed;
    }

    /// <summary>
    /// The reducer's budget for this turn: <c>resolvedContext - maxOutput - ReservedPromptTokens</c>,
    /// never above <c>ChatHistoryOptions.MaxHistoryTokens</c>.
    /// </summary>
    /// <param name="edge">The registration's options.</param>
    /// <param name="resolvedContext">The context the budget allowed.</param>
    /// <param name="maxOutput">The per-turn output cap.</param>
    /// <returns>A per-turn copy of the history options with the budget applied.</returns>
    public static ChatHistoryOptions HistoryOptionsForTurn(EdgeChatOptions edge, int resolvedContext, int maxOutput)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var budget = Math.Max(0, resolvedContext - maxOutput - edge.ReservedPromptTokens);
        var history = edge.History;

        var forTurn = new ChatHistoryOptions
        {
            MaxTurns = history.MaxTurns,
            MaxHistoryTokens = Math.Min(history.MaxHistoryTokens, budget),
            PreserveSystemMessages = history.PreserveSystemMessages,
            MinimumPreservedMessages = history.MinimumPreservedMessages,
        };

        forTurn.PinnedMessageKeys.Clear();
        foreach (var key in history.PinnedMessageKeys)
        {
            forTurn.PinnedMessageKeys.Add(key);
        }

        return forTurn;
    }

    // ---- (d) Format ------------------------------------------------------------------------------------------

    /// <summary>The <c>[{"role","content"}]</c> array the model's template takes. Source-free JSON writing.</summary>
    /// <param name="messages">The reduced conversation.</param>
    /// <returns>The JSON text.</returns>
    public static string MessagesToJson(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role.Value);
                writer.WriteString("content", message.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// The role-prefixed fallback, used only when the template probe failed and
    /// <c>RequireChatTemplate</c> was cleared. Materially worse output, and named as such in
    /// diagnostics.
    /// </summary>
    /// <param name="messages">The reduced conversation.</param>
    /// <returns>The prompt text.</returns>
    public static string FallbackFormat(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append(message.Role.Value).Append(": ").Append(message.Text).Append('\n');
        }

        builder.Append("assistant: ");
        return builder.ToString();
    }

    /// <summary>The reduced list as one Trace-only string.</summary>
    /// <param name="messages">The reduced conversation.</param>
    /// <returns>Role and text per line.</returns>
    public static string DescribeMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append('[').Append(message.Role.Value).Append("] ").Append(message.Text).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Spec section 15.3's 7102 row: the reducer's floor still does not fit a fresh generator.</summary>
    /// <param name="presetId">The preset.</param>
    /// <param name="promptTokens">The prompt's token count.</param>
    /// <param name="resolvedContext">The context the budget allowed.</param>
    /// <param name="maxOutput">The per-turn output cap.</param>
    /// <returns>The exception to throw.</returns>
    public static EdgeChatException PromptTooLong(string presetId, int promptTokens, int resolvedContext, int maxOutput)
    {
        var budget = resolvedContext - maxOutput;
        return new EdgeChatException(
            EdgeErrorCode.ChatPromptTooLong,
            $"The prompt is {promptTokens} tokens after history reduction reached its floor, and the " +
            $"budget for it is {budget} tokens ({resolvedContext} of context minus {maxOutput} for " +
            "the answer). The floor keeps system messages, pinned messages and the newest " +
            "MinimumPreservedMessages, so a pinned RAG context block bigger than the budget lands " +
            "here rather than being silently evicted.")
        {
            PresetId = presetId,
            PromptTokens = promptTokens,
            RequestedContextTokens = resolvedContext,
            FittingContextTokens = budget,
            Remediation =
                "Lower RagOptions.MaxContextTokens so the grounding fits, lower MaxOutputTokens to " +
                "leave more room for the prompt, or tighten ChatHistoryOptions " +
                "(MaxHistoryTokens, MinimumPreservedMessages).",
        };
    }

    // ---- (e) Params -----------------------------------------------------------------------------------------

    /// <summary>Spec section 6.6's precedence table: per-call, then this client, then the preset default.</summary>
    /// <param name="options">The per-call options, or null.</param>
    /// <param name="edge">The registration's options.</param>
    /// <returns>The resolved knobs.</returns>
    public static ChatTurnKnobs ResolveKnobs(ChatOptions? options, EdgeChatOptions edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var preset = edge.Preset;

        return new ChatTurnKnobs(
            options?.MaxOutputTokens ?? edge.MaxOutputTokens ?? preset.DefaultMaxOutputTokens,
            options?.Temperature ?? edge.Temperature ?? preset.DefaultTemperature,
            options?.TopP ?? edge.TopP ?? preset.DefaultTopP,
            options?.TopK ?? edge.TopK ?? preset.DefaultTopK,
            options?.Seed,
            options?.PresencePenalty);
    }

    /// <summary>
    /// The search options in application order: the mapped knobs, then the <c>bool</c> and
    /// <c>double</c> entries of <c>ChatOptions.AdditionalProperties</c> (everything else skipped
    /// without error), then <c>EdgeChatOptions.SearchOptions</c> last - converted or refused, never
    /// dropped, and <c>max_length</c> refused whatever its type.
    /// </summary>
    /// <param name="knobs">The resolved knobs.</param>
    /// <param name="maxLength">The <c>max_length</c> this generator is built with.</param>
    /// <param name="options">The per-call options, or null.</param>
    /// <param name="edge">The registration's options.</param>
    /// <returns>The entries, each value a <see cref="bool"/> or a <see cref="double"/>.</returns>
    /// <exception cref="EdgeChatException">7108 naming the key.</exception>
    public static IReadOnlyList<KeyValuePair<string, object>> ComposeSearchOptions(
        ChatTurnKnobs knobs,
        int maxLength,
        ChatOptions? options,
        EdgeChatOptions edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var composed = new OrderedDictionary<string, object>(StringComparer.Ordinal)
        {
            [GenAiConfigOverlay.MaxLengthKey] = (double)maxLength,
            ["temperature"] = (double)knobs.Temperature,
            ["top_p"] = (double)knobs.TopP,
            ["top_k"] = (double)knobs.TopK,
            ["do_sample"] = true,
        };

        if (knobs.Seed is { } seed)
        {
            composed["random_seed"] = (double)seed;
        }

        if (knobs.RepetitionPenalty is { } penalty)
        {
            composed["repetition_penalty"] = (double)penalty;
        }

        // The open bag shared with the rest of the MEAI pipeline: only a bool or a double is a
        // search option; a RagSource list or anything else is somebody else's and is left alone.
        if (options?.AdditionalProperties is { } additional)
        {
            foreach (var (key, value) in additional)
            {
                if (string.Equals(key, GenAiConfigOverlay.MaxLengthKey, StringComparison.Ordinal))
                {
                    continue;
                }

                switch (value)
                {
                    case bool flag:
                        composed[key] = flag;
                        break;
                    case double number:
                        composed[key] = number;
                        break;
                }
            }
        }

        // The escape hatch: nothing else writes here, so a typo must not become a value that was
        // never applied.
        foreach (var (key, value) in edge.SearchOptions)
        {
            if (string.Equals(key, GenAiConfigOverlay.MaxLengthKey, StringComparison.Ordinal))
            {
                throw OptionUnsupported(
                    "EdgeChatOptions.SearchOptions declares max_length, which is the KV memory cap the " +
                    "budget owns",
                    "Remove max_length; set EdgeChatOptions.MaxContextTokens to bound the context, or " +
                    "MaxOutputTokens to bound the answer.");
            }

            composed[key] = ConvertSearchOption(key, value);
        }

        return [.. composed];
    }

    /// <summary>
    /// <c>bool</c> and <c>double</c> pass; <c>int</c>, <c>long</c>, <c>short</c>, <c>byte</c>,
    /// <c>float</c> and <c>decimal</c> convert when the conversion round-trips exactly; everything
    /// else is 7108 naming the key and the CLR type.
    /// </summary>
    /// <param name="key">The search option.</param>
    /// <param name="value">Its value as the caller typed it.</param>
    /// <returns>A <see cref="bool"/> or a <see cref="double"/>.</returns>
    /// <exception cref="EdgeChatException">7108.</exception>
    public static object ConvertSearchOption(string key, object? value)
    {
        switch (value)
        {
            case bool flag:
                return flag;
            case double number:
                return number;
            case int i:
                return (double)i;
            case short s:
                return (double)s;
            case byte b:
                return (double)b;
            case long l:
                {
                    var converted = (double)l;
                    if (Math.Abs(l) <= (1L << 53) && (long)converted == l)
                    {
                        return converted;
                    }

                    throw Lossy(key, l.ToString(CultureInfo.InvariantCulture), "long");
                }

            case float f:
                {
                    var converted = (double)f;
                    if (!float.IsNaN(f) && (float)converted == f)
                    {
                        return converted;
                    }

                    throw Lossy(key, f.ToString(CultureInfo.InvariantCulture), "float");
                }

            case decimal m:
                {
                    var converted = (double)m;
                    if (TryRoundTrip(converted, m))
                    {
                        return converted;
                    }

                    throw Lossy(key, m.ToString(CultureInfo.InvariantCulture), "decimal");
                }

            default:
                throw OptionUnsupported(
                    $"EdgeChatOptions.SearchOptions['{key}'] is {value?.GetType().FullName ?? "null"}, and " +
                    "the native API has exactly two overloads: SetSearchOption(string, double) and " +
                    "SetSearchOption(string, bool)",
                    "Type the value as a bool or a double. This dictionary refuses rather than drops, so a " +
                    "\"0.7\" typed as a string never becomes a temperature that was silently not applied.");
        }
    }

    private static bool TryRoundTrip(double converted, decimal original)
    {
        try
        {
            return (decimal)converted == original;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static EdgeChatException Lossy(string key, string value, string clrType) =>
        OptionUnsupported(
            $"EdgeChatOptions.SearchOptions['{key}'] = {value} ({clrType}) does not convert to a double " +
            "exactly, and the native API takes a double",
            "Pass a value that a double represents exactly, or type it as a double yourself.");

    // ---- Exceptions ------------------------------------------------------------------------------------

    /// <summary>7108, with the member named in the message.</summary>
    /// <param name="what">What was unsupported.</param>
    /// <param name="remediation">What to do instead.</param>
    /// <returns>The exception to throw.</returns>
    public static EdgeChatException OptionUnsupported(string what, string remediation) =>
        new(EdgeErrorCode.ChatOptionUnsupported, $"This chat client cannot honour the request: {what}.")
        {
            Remediation = remediation,
        };

    /// <summary>7105, naming which of the four busy conditions fired.</summary>
    /// <param name="which">The condition.</param>
    /// <param name="remediation">What to do instead.</param>
    /// <returns>The exception to throw.</returns>
    public static EdgeChatException Busy(string which, string remediation) =>
        new(
            EdgeErrorCode.ChatBusy,
            $"The chat model is busy: {which}. The ORT GenAI C API is not thread safe, so turns " +
            "serialise on one gate per model rather than allocating a second KV cache.")
        {
            Remediation = remediation,
        };

    private static EdgeChatException ThermalAbort(EdgeThermalState thermal, string why) =>
        new(
            EdgeErrorCode.ChatThermalAbort,
            $"The turn was refused before anything was allocated: {why}.")
        {
            Thermal = thermal,
            Remediation =
                "Let the device cool, raise ChatThermalOptions.AbortAt, or clear " +
                "RefuseWhenThermalUnknown if that was the switch that fired.",
        };

    private static EdgeChatException ToolCallingUnsupported(string what) =>
        new(
            EdgeErrorCode.ChatToolCallingUnsupported,
            $"Tool calling is not supported by this client: {what}. A required tool call that can " +
            "never be emitted is a hard failure, not a footnote.")
        {
            Remediation =
                "Remove ChatOptions.Tools and leave ToolMode at Auto or None. Tool calling is out of " +
                "scope for v1 (ADR 0012).",
        };
}
