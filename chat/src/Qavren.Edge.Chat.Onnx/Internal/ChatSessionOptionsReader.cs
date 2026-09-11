using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Reads <c>model.decoder.session_options.intra_op_num_threads</c> out of a genai_config.json body.
/// <para>
/// <b>The block is NOT at the top level.</b> Section 14.3 says "the resolved genai_config.json's
/// <c>session_options</c>", which is shorthand: an ORT GenAI config's top level is <c>model</c> and
/// <c>search</c>, and this block sits under <c>model.decoder</c> beside <c>filename</c>,
/// <c>head_size</c> and <c>num_hidden_layers</c>. The path is hard-coded here and nowhere else.
/// </para>
/// <para>
/// Source-generated, exactly as <see cref="ChatModelShape"/>'s parsing is, so the load path takes
/// no reflection. It is handed the config text the loader ALREADY read - it never opens the file a
/// second time - and the answer is cached on <c>ChatModelInfo</c> beside the shape, so the
/// diagnostics contributor re-parses nothing.
/// </para>
/// <para>
/// This is deliberately not a field on <see cref="ChatModelShape"/>: every field there is
/// cross-checked against the provisioned config and a mismatch is 7008, and a thread count is a
/// runtime hint a publisher may change between revisions without changing the model.
/// </para>
/// </summary>
internal static class ChatSessionOptionsReader
{
    /// <summary>What the diagnostics contributor publishes when the config declares nothing.</summary>
    /// <remarks>
    /// Never <c>"0"</c>: zero is ORT's "pick a thread count for me" sentinel and is a real,
    /// different answer from "the publisher said nothing".
    /// </remarks>
    public const string NotDeclared = "(not declared)";

    /// <summary>Reads the declared intra-op thread count, if there is one.</summary>
    /// <param name="genAiConfigJson">The config text the load path already has in hand.</param>
    /// <returns>
    /// The declared value, or <see langword="null"/> when <c>session_options</c> is absent, when
    /// <c>intra_op_num_threads</c> is absent, or when it is not a number. <b>Never 0</b> is
    /// substituted for absence: 0 is ORT's "pick for me" sentinel and a real, different answer.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="genAiConfigJson"/> is null.</exception>
    public static int? ReadIntraOpNumThreads(string genAiConfigJson)
    {
        ArgumentNullException.ThrowIfNull(genAiConfigJson);

        try
        {
            var document = JsonSerializer.Deserialize(
                genAiConfigJson,
                ChatSessionOptionsJsonContext.Default.ChatSessionOptionsDocument);

            return document?.Model?.Decoder?.SessionOptions?.IntraOpNumThreads;
        }
        catch (JsonException)
        {
            // A config this reader cannot parse is the shape reader's failure to report, not this
            // one's: 7007 already names the file and the field. A diagnostics key is never the
            // thing that decides a load.
            return null;
        }
    }

    /// <summary>Formats the answer for the diagnostics block.</summary>
    /// <param name="intraOpNumThreads">The reader's answer.</param>
    /// <returns>The number, or <see cref="NotDeclared"/>.</returns>
    public static string Describe(int? intraOpNumThreads) =>
        intraOpNumThreads is { } threads
            ? threads.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : NotDeclared;
}

/// <summary>The three nodes on the path, and nothing else. Every level nullable.</summary>
/// <remarks>
/// A config missing <c>model</c>, <c>decoder</c> or <c>session_options</c> deserialises to
/// <see langword="null"/> at that level rather than throwing, which is why the reader needs no
/// existence checks of its own.
/// </remarks>
internal sealed class ChatSessionOptionsDocument
{
    /// <summary>The <c>model</c> block.</summary>
    [JsonPropertyName("model")]
    public ChatSessionOptionsModel? Model { get; set; }
}

/// <summary>The <c>model</c> block, carrying only <c>decoder</c>.</summary>
internal sealed class ChatSessionOptionsModel
{
    /// <summary>The <c>model.decoder</c> block.</summary>
    [JsonPropertyName("decoder")]
    public ChatSessionOptionsDecoder? Decoder { get; set; }
}

/// <summary>The <c>model.decoder</c> block, carrying only <c>session_options</c>.</summary>
internal sealed class ChatSessionOptionsDecoder
{
    /// <summary>The <c>model.decoder.session_options</c> block.</summary>
    [JsonPropertyName("session_options")]
    public ChatSessionOptionsBlock? SessionOptions { get; set; }
}

/// <summary>The <c>session_options</c> block, carrying only the one key section 14.3 publishes.</summary>
internal sealed class ChatSessionOptionsBlock
{
    /// <summary>
    /// <c>intra_op_num_threads</c>, or null when absent. A non-number here - a string, say, which
    /// is legal JSON and an illegal ORT config - makes the deserialise throw, and the reader turns
    /// that into null rather than into a load failure: a diagnostics key must never be the reason a
    /// model does not load.
    /// </summary>
    [JsonPropertyName("intra_op_num_threads")]
    public int? IntraOpNumThreads { get; set; }
}

/// <summary>
/// The source-generated contract for <see cref="ChatSessionOptionsReader"/>. Source-generated and
/// never reflection-based, exactly as <c>ChatModelShapeJsonContext</c> is: the load path must stay
/// clean of IL2026/IL3050 under <c>TreatWarningsAsErrors</c>.
/// </summary>
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ChatSessionOptionsDocument))]
internal sealed partial class ChatSessionOptionsJsonContext : JsonSerializerContext;
