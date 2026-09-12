using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Qavren.Edge.Chat;

/// <summary>
/// The decoder geometry the memory budget is computed from. A preset declares it - the budget has
/// to refuse <i>before</i> 1.24 GB is mapped, and reading the shape at load time is too late -
/// and the loader cross-checks the declaration against the provisioned <c>genai_config.json</c>
/// field by field, so a republished model folder is
/// <see cref="EdgeErrorCode.ChatModelShapeMismatch"/> rather than a wrong budget.
/// </summary>
public sealed record ChatModelShape
{
    /// <summary>The name every message the shape reader and the cross-check raise refers to.</summary>
    private const string ConfigFileName = "genai_config.json";

    /// <summary><c>model.type</c>: "llama", "qwen3", "phi3". Diagnostics only.</summary>
    public required string ModelType { get; init; }

    /// <summary><c>model.context_length</c>. The hard ceiling on <c>search.max_length</c>.</summary>
    public required int ContextLength { get; init; }

    /// <summary><c>model.vocab_size</c>.</summary>
    public required int VocabSize { get; init; }

    /// <summary><c>model.decoder.num_hidden_layers</c>.</summary>
    public required int NumHiddenLayers { get; init; }

    /// <summary><c>model.decoder.num_key_value_heads</c>.</summary>
    public required int NumKeyValueHeads { get; init; }

    /// <summary><c>model.decoder.head_size</c>.</summary>
    public required int HeadSize { get; init; }

    /// <summary><c>model.decoder.sliding_window</c> when the model declares one; null otherwise.</summary>
    public int? SlidingWindow { get; init; }

    /// <summary><c>model.decoder.filename</c>.</summary>
    public required string DecoderFileName { get; init; }

    /// <summary>
    /// Bytes per KV cache element. Default 2 (fp16).
    /// <para>
    /// <b>This is the one field the cross-check cannot validate.</b> <c>genai_config.json</c>
    /// states layers, KV heads and head size but says nothing about the KV dtype, so a
    /// disagreement here is undetectable at load and shows up only as a budget that is wrong by a
    /// factor of two. 2 is the calibrated default, not a guess: for
    /// <c>Llama32_1BInstructInt4</c> it puts the arithmetic within 1.2% of a measured 1280 MiB
    /// peak, where 4 would put it 11% over. Spec section 19 item 4 is the nightly measurement that
    /// answers it per preset, and <see cref="MeasuredPeakBytes"/> is what absorbs the error
    /// meanwhile.
    /// </para>
    /// </summary>
    public int KvCacheBytesPerElement { get; init; } = 2;

    /// <summary>
    /// Resident weight estimate: the sum of the manifest's <c>Graph</c> and
    /// <c>GraphExternalData</c> file sizes, and <b>not</b> <c>TotalSizeBytes</c> - a 17 MB
    /// <c>tokenizer.json</c> is not resident weights, and counting it would over-report by tens of
    /// megabytes inside a user-visible refusal message.
    /// </summary>
    public required long WeightsBytes { get; init; }

    /// <summary>
    /// A peak RSS measured on a real device at <see cref="ContextLength"/>, or null. When present
    /// and <see cref="ChatMemoryBudgetOptions.PreferMeasuredPeak"/> is set, the budget takes
    /// <c>max(arithmetic, measured + ReserveBytes)</c> - spec section 9.3 is the normative
    /// formula - and records which term bound in <see cref="ChatMemoryDecision.UsedMeasuredPeak"/>.
    /// The reserve is added to the measured term because a measured peak is the model's own
    /// resident footprint and carries no headroom for the rest of the app. Pure arithmetic
    /// underestimates large-vocabulary models badly - Gemma-3-1b computes ~929 MiB and measures
    /// 1502 MiB - so a preset without a measurement is running on arithmetic alone and the
    /// diagnostics block says so.
    /// <para>
    /// <b>For both shipped presets this term is inert today, and that is stated rather than
    /// implied.</b> <c>Llama32_1BInstructInt4</c>'s measured 1280 MiB plus a 192 MiB reserve is
    /// 1472 MiB, below its own arithmetic of 1679 MiB at 4096 tokens, so the arithmetic binds;
    /// <c>Qwen3_600MInt4</c> has no device measurement at all. The machinery exists for the case it
    /// was built for - a large-vocabulary preset whose arithmetic under-reports, which is exactly
    /// Gemma-3-1b - and spec section 19 item 3 is what would make it bind here.
    /// </para>
    /// </summary>
    public long? MeasuredPeakBytes { get; init; }

    /// <summary>
    /// What device and runtime <see cref="MeasuredPeakBytes"/> came from. Never a guess.
    /// <para>
    /// It is a diagnostics string and <b>nothing else</b>: spec section 9.3's formula reads
    /// <see cref="MeasuredPeakBytes"/> alone and cannot discount a measurement by provenance. So
    /// the rule is on the way in, not on the way out - <see cref="MeasuredPeakBytes"/> is set only
    /// from a measurement taken on a device of the class the preset is meant to run on. A server
    /// or laptop peak is recorded here with <see cref="MeasuredPeakBytes"/> left null, which is
    /// what <c>Qwen3_600MInt4</c> does with its published Graviton figure.
    /// </para>
    /// </summary>
    public string? MeasuredOn { get; init; }

    /// <summary>layers x 2 (K and V) x kvHeads x headSize x bytesPerElement.</summary>
    public long KvCacheBytesPerToken =>
        (long)NumHiddenLayers * 2 * NumKeyValueHeads * HeadSize * KvCacheBytesPerElement;

    /// <summary>KV bytes for a context, clamped by <see cref="SlidingWindow"/> when the model declares one.</summary>
    /// <param name="contextTokens">The context length in tokens.</param>
    /// <returns>The KV cache size in bytes at that context.</returns>
    public long KvCacheBytes(int contextTokens) =>
        KvCacheBytesPerToken * Math.Min(contextTokens, SlidingWindow ?? int.MaxValue);

    /// <summary>Reads the geometry out of a <c>genai_config.json</c> body.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="weightsBytes">
    /// The preset manifest's <c>Graph</c> plus <c>GraphExternalData</c> sizes. The config does not
    /// state it, so it is supplied rather than read.
    /// </param>
    /// <returns>The parsed shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="EdgeChatException">
    /// <see cref="EdgeErrorCode.ChatConfigurationInvalid"/> (7007), naming the file and the
    /// offending field.
    /// </exception>
    public static ChatModelShape FromGenAiConfig(string json, long weightsBytes)
    {
        if (TryFromGenAiConfig(json, weightsBytes, out var shape, out var error))
        {
            return shape;
        }

        throw new EdgeChatException(EdgeErrorCode.ChatConfigurationInvalid, error)
        {
            Remediation =
                "Re-provision the model folder, or re-run " +
                "chat/tools/model-hashes/fetch_chat_model_hashes.py when the publisher has " +
                "changed the export.",
        };
    }

    /// <summary>Reads the geometry out of a <c>genai_config.json</c> body without throwing.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="weightsBytes">The preset manifest's resident-weight total.</param>
    /// <param name="shape">The parsed shape, or null.</param>
    /// <param name="error">Null on success; otherwise a message naming the file and the field.</param>
    /// <returns><see langword="true"/> when the config parsed and is coherent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static bool TryFromGenAiConfig(
        string json,
        long weightsBytes,
        [NotNullWhen(true)] out ChatModelShape? shape,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(json);

        shape = null;

        GenAiConfigDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, ChatModelShapeJsonContext.Default.GenAiConfigDocument);
        }
        catch (JsonException ex)
        {
            // Path reads "$.model.context_length" for a non-numeric context length, which is
            // exactly the field spec section 15.3 requires the message to name.
            var field = ex.Path is { Length: > 0 } path ? path.TrimStart('$', '.') : null;
            error = field is { Length: > 0 }
                ? Invalid(FormattableString.Invariant($"'{field}' could not be read: {ex.Message}"))
                : Invalid(FormattableString.Invariant($"the file is not valid JSON: {ex.Message}"));
            return false;
        }

        if (document?.Model is not { } model)
        {
            error = Invalid("'model' is missing.");
            return false;
        }

        if (model.Decoder is not { } decoder)
        {
            error = Invalid("'model.decoder' is missing.");
            return false;
        }

        if (model.Type is not { Length: > 0 } modelType)
        {
            error = Invalid("'model.type' is missing or empty.");
            return false;
        }

        if (model.ContextLength is not { } contextLength || contextLength <= 0)
        {
            error = Invalid("'model.context_length' is missing or not a positive integer.");
            return false;
        }

        if (model.VocabSize is not { } vocabSize || vocabSize <= 0)
        {
            error = Invalid("'model.vocab_size' is missing or not a positive integer.");
            return false;
        }

        if (!IsReadableEosTokenId(model.EosTokenId))
        {
            error = Invalid("'model.eos_token_id' is neither an integer nor an array of integers.");
            return false;
        }

        if (decoder.FileName is not { Length: > 0 } decoderFileName)
        {
            error = Invalid("'model.decoder.filename' is missing or empty.");
            return false;
        }

        if (decoder.NumHiddenLayers is not { } layers || layers <= 0)
        {
            error = Invalid("'model.decoder.num_hidden_layers' is missing or not a positive integer.");
            return false;
        }

        if (decoder.NumKeyValueHeads is not { } kvHeads || kvHeads <= 0)
        {
            error = Invalid("'model.decoder.num_key_value_heads' is missing or not a positive integer.");
            return false;
        }

        if (decoder.HeadSize is not { } headSize || headSize <= 0)
        {
            error = Invalid("'model.decoder.head_size' is missing or not a positive integer.");
            return false;
        }

        shape = new ChatModelShape
        {
            ModelType = modelType,
            ContextLength = contextLength,
            VocabSize = vocabSize,
            NumHiddenLayers = layers,
            NumKeyValueHeads = kvHeads,
            HeadSize = headSize,
            SlidingWindow = ReadSlidingWindow(decoder.SlidingWindow),
            DecoderFileName = decoderFileName,
            WeightsBytes = weightsBytes,
        };

        error = null;
        return true;
    }

    /// <summary>
    /// Spec section 9.1 step 3's cross-check: every field the config states, compared against the
    /// preset's declaration. <see cref="KvCacheBytesPerElement"/>, <see cref="WeightsBytes"/>,
    /// <see cref="MeasuredPeakBytes"/> and <see cref="MeasuredOn"/> are NOT compared - the config
    /// states none of them, which for the first is the documented exception in spec section 6.2.
    /// </summary>
    /// <param name="presetId">The preset whose declaration is being checked.</param>
    /// <param name="preset">The preset's declared shape.</param>
    /// <param name="fromConfig">The shape read from the provisioned config.</param>
    /// <returns>Null when the two agree; otherwise a message naming the field and both values.</returns>
    internal static string? DescribeMismatch(string presetId, ChatModelShape preset, ChatModelShape fromConfig)
    {
        return Compare("model.type", fromConfig.ModelType, preset.ModelType)
            ?? Compare("model.context_length", Text(fromConfig.ContextLength), Text(preset.ContextLength))
            ?? Compare("model.vocab_size", Text(fromConfig.VocabSize), Text(preset.VocabSize))
            ?? Compare("model.decoder.num_hidden_layers", Text(fromConfig.NumHiddenLayers), Text(preset.NumHiddenLayers))
            ?? Compare("model.decoder.num_key_value_heads", Text(fromConfig.NumKeyValueHeads), Text(preset.NumKeyValueHeads))
            ?? Compare("model.decoder.head_size", Text(fromConfig.HeadSize), Text(preset.HeadSize))
            ?? Compare("model.decoder.sliding_window", Text(fromConfig.SlidingWindow), Text(preset.SlidingWindow))
            ?? Compare("model.decoder.filename", fromConfig.DecoderFileName, preset.DecoderFileName);

        string? Compare(string field, string declared, string expected) =>
            string.Equals(declared, expected, StringComparison.Ordinal)
                ? null
                : FormattableString.Invariant(
                    $"{ConfigFileName} declares {field} = {declared}, but preset '{presetId}' declares {expected}.");
    }

    /// <summary>Throws <see cref="EdgeErrorCode.ChatModelShapeMismatch"/> (7008) when they disagree.</summary>
    /// <param name="presetId">The preset whose declaration is being checked.</param>
    /// <param name="preset">The preset's declared shape.</param>
    /// <param name="fromConfig">The shape read from the provisioned config.</param>
    /// <exception cref="EdgeChatException">The two shapes disagree on at least one field.</exception>
    internal static void ThrowIfMismatched(string presetId, ChatModelShape preset, ChatModelShape fromConfig)
    {
        if (DescribeMismatch(presetId, preset, fromConfig) is not { } mismatch)
        {
            return;
        }

        throw new EdgeChatException(EdgeErrorCode.ChatModelShapeMismatch, mismatch)
        {
            PresetId = presetId,
            Remediation =
                "Somebody repointed the preset at a different export. Re-run " +
                "chat/tools/model-hashes/fetch_chat_model_hashes.py and commit the regenerated " +
                "ChatPresets.g.cs.",
        };
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "(absent)";

    private static string Invalid(string detail) =>
        FormattableString.Invariant($"{ConfigFileName} is not a usable ORT GenAI configuration: {detail}");

    private static bool IsReadableEosTokenId(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                // Absent is legal. Nothing on the geometry path reads it; the field is normalised
                // here only so that an export spelling it as a scalar and an export spelling it as
                // an array both parse, which is what both shipped presets and spec section 6.2
                // require.
                return true;

            case JsonValueKind.Number:
                return element.TryGetInt32(out _);

            case JsonValueKind.Array:
                foreach (var entry in element.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt32(out _))
                    {
                        return false;
                    }
                }

                return true;

            default:
                return false;
        }
    }

    private static int? ReadSlidingWindow(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var window) && window > 0)
        {
            return window;
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("window_size", out var size)
            && size.ValueKind == JsonValueKind.Number
            && size.TryGetInt32(out var windowSize)
            && windowSize > 0)
        {
            return windowSize;
        }

        // Anything else - absent, null, or a spelling this reader does not recognise - is "no
        // sliding window", which is the conservative answer: it leaves the KV cache unclamped, so
        // the budget over-states rather than under-states what a turn will cost.
        return null;
    }
}
