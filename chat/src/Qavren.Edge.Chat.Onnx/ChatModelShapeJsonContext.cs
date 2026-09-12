using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qavren.Edge.Chat;

/// <summary>The top level of an ORT GenAI <c>genai_config.json</c>: <c>model</c> and <c>search</c>.</summary>
/// <remarks>
/// <c>search</c> is deliberately absent. Spec section 9.1 step 5 re-emits the shipped search block
/// verbatim as raw JSON when it composes the single <c>Config.Overlay</c> document, so nothing on
/// the load path needs it typed - and a typed copy would be a second thing to keep in step with a
/// publisher's revisions.
/// </remarks>
internal sealed class GenAiConfigDocument
{
    [JsonPropertyName("model")]
    public GenAiModelSection? Model { get; set; }
}

/// <summary>The <c>model</c> block: the decoder geometry, and nothing that is not geometry.</summary>
internal sealed class GenAiModelSection
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }

    [JsonPropertyName("vocab_size")]
    public int? VocabSize { get; set; }

    /// <summary>
    /// Kept as a <see cref="JsonElement"/> because the field is an <c>int</c> in some exports and
    /// an <b>array</b> in others - as both shipped presets prove, one with three entries and one
    /// with two. Normalising happens in <see cref="ChatModelShape.TryFromGenAiConfig"/>; a typed
    /// <c>int?</c> here would make every real config a parse failure.
    /// </summary>
    [JsonPropertyName("eos_token_id")]
    public JsonElement EosTokenId { get; set; }

    [JsonPropertyName("decoder")]
    public GenAiDecoderSection? Decoder { get; set; }
}

/// <summary>
/// The <c>model.decoder</c> block.
/// </summary>
/// <remarks>
/// <b><c>session_options</c> is deliberately absent, and this is the paragraph that exists so the
/// next reader does not "fix" it (plan adjustment 29).</b> That block sits right here, beside
/// <c>filename</c> and <c>head_size</c>, and it carries <c>intra_op_num_threads</c> - which spec
/// section 14.3 publishes as the diagnostics key <c>chatIntraOpNumThreads</c>. It is read by
/// <c>Internal\ChatSessionOptionsReader.cs</c> through its own source-generated context, not here,
/// because every field on <see cref="ChatModelShape"/> is cross-checked field by field against the
/// provisioned config and a disagreement is <c>ChatModelShapeMismatch</c> (7008). A thread count is
/// a runtime hint a publisher may change between revisions without changing the model, so carrying
/// it here would turn a harmless republish into a hard 7008 - and every <see cref="ChatPreset"/>
/// would have to declare a value it has no business declaring.
/// </remarks>
internal sealed class GenAiDecoderSection
{
    [JsonPropertyName("filename")]
    public string? FileName { get; set; }

    [JsonPropertyName("head_size")]
    public int? HeadSize { get; set; }

    [JsonPropertyName("num_hidden_layers")]
    public int? NumHiddenLayers { get; set; }

    [JsonPropertyName("num_key_value_heads")]
    public int? NumKeyValueHeads { get; set; }

    /// <summary>
    /// A <see cref="JsonElement"/> rather than an <c>int?</c> for the same reason
    /// <see cref="GenAiModelSection.EosTokenId"/> is: exports that declare a sliding window spell
    /// it either as a bare number or as an object carrying <c>window_size</c>. Neither shipped
    /// preset declares one at all.
    /// </summary>
    [JsonPropertyName("sliding_window")]
    public JsonElement SlidingWindow { get; set; }
}

/// <summary>
/// The source-generated contract for <see cref="ChatModelShape.FromGenAiConfig"/>. Source-generated
/// and never reflection-based, so the load path stays trim- and AOT-safe: spec section 17 requires
/// the package to publish clean of IL2026/IL3050 under <c>TreatWarningsAsErrors</c>, and one
/// reflection-based <c>JsonSerializer.Deserialize</c> on this path would cost that.
/// </summary>
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(GenAiConfigDocument))]
internal sealed partial class ChatModelShapeJsonContext : JsonSerializerContext;
