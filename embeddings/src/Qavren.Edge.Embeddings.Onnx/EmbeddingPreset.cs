using Qavren.Edge.Onnx;

namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>
/// Everything the ONNX graph does not tell you. A wrong preset is a silent quality bug, so every
/// field that changes the numbers is <c>required</c>.
/// </summary>
public sealed record EmbeddingPreset
{
    /// <summary>The stable id <see cref="EmbeddingPresets.ById"/> resolves.</summary>
    public required string Id { get; init; }

    /// <summary>The pinned manifest: repo, commit SHA, per-file size and digest, SPDX licence.</summary>
    public required OnnxModelManifest Manifest { get; init; }

    /// <summary>The manifest-relative path of the graph ORT is handed.</summary>
    public required string ModelFile { get; init; }

    /// <summary>The manifest-relative path of the tokenizer asset.</summary>
    public required string TokenizerFile { get; init; }

    /// <summary>Which tokenizer family <see cref="TokenizerFile"/> is.</summary>
    public required EdgeTokenizerKind TokenizerKind { get; init; }

    /// <summary>Whether the model was trained on lower-cased text.</summary>
    public required bool LowerCase { get; init; }

    /// <summary>The embedding width. Fixed: this library implements no Matryoshka truncation.</summary>
    public required int Dimensions { get; init; }

    /// <summary>The longest sequence the graph accepts, special tokens included.</summary>
    public required int MaxSequenceLength { get; init; }

    /// <summary>
    /// How the batch pools. From the preset, NEVER a default: bge-small is CLS and everything else
    /// here is mean, and getting it wrong is a silent retrieval regression with no exception
    /// anywhere.
    /// </summary>
    /// <remarks>
    /// Read ONLY when the graph's declared output is rank-3 <c>[batch, sequence, dim]</c>, which is
    /// what <c>last_hidden_state</c> is and what all four shipped presets emit. A graph whose
    /// output is rank-2 <c>[batch, dim]</c> has pooled in its own head - a
    /// <c>sentence_embedding</c> / <c>pooler_output</c> export - and there is no sequence axis left
    /// to pool over, so the row is taken verbatim and this property is not applied. Layer-norm and
    /// L2 still run on both paths. Spec 11 describes the <c>last_hidden_state</c> path only; the
    /// rank-2 path is an extension this implementation carries so a consumer's own in-graph-pooled
    /// model yields a correct vector instead of an <see cref="ArgumentOutOfRangeException"/> from a
    /// stride computed for three ranks, and spec 11 needs the sentence.
    /// </remarks>
    public required EmbeddingPooling Pooling { get; init; }

    /// <summary>
    /// Apply <see cref="EmbeddingPooler.LayerNorm"/> between pooling and L2 normalisation.
    /// <b>Not</b> Matryoshka truncation, which is cut - this is an unconditional step in
    /// nomic-embed-text-v1.5's reference pooling. Its <c>modules.json</c> carries only Transformer
    /// + Pooling (no Normalize module) and its documented pipeline is mean-pool, then
    /// <c>F.layer_norm</c> over the 768 dim, then L2. Omitting it ships plausible-but-off vectors
    /// with no error anywhere, which is exactly what the <c>required</c> fields on this record
    /// exist to prevent. False for the three MiniLM/BGE presets.
    /// </summary>
    public bool PostPoolLayerNorm { get; init; }

    /// <summary>
    /// Epsilon for <see cref="PostPoolLayerNorm"/>. 1e-5 is PyTorch's default and what nomic's
    /// reference uses. Read ONLY when <see cref="PostPoolLayerNorm"/> is set, so three of the four
    /// shipped presets never touch it.
    /// </summary>
    public float LayerNormEpsilon { get; init; } = 1e-5f;

    /// <summary>L2-normalise the pooled vector. True for all four shipped presets.</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>The instruction prefix the query-side generator prepends, or null.</summary>
    public string? QueryPrefix { get; init; }

    /// <summary>The instruction prefix the document-side generator prepends, or null.</summary>
    public string? DocumentPrefix { get; init; }

    /// <summary>The graph's token-id input name. A BINDING KEY: inputs are bound by name, never by position.</summary>
    public string InputIdsName { get; init; } = "input_ids";

    /// <summary>The graph's attention-mask input name. A binding key.</summary>
    public string AttentionMaskName { get; init; } = "attention_mask";

    /// <summary>
    /// The graph's token-type input name, or null when the graph declares no such input - in which
    /// case the generator binds two tensors instead of three. Bound BY NAME, never by position:
    /// nomic declares <c>(input_ids, token_type_ids, attention_mask)</c> while MiniLM declares
    /// <c>(input_ids, attention_mask, token_type_ids)</c>.
    /// </summary>
    public string? TokenTypeIdsName { get; init; } = "token_type_ids";

    /// <summary>The graph's output name. A binding key.</summary>
    public string OutputName { get; init; } = "last_hidden_state";

    /// <summary>
    /// The padded widths a batch rounds up to, ascending. What the batch assembler rounds to and
    /// what <see cref="OnnxEmbeddingOptions.PinnedSequenceLength"/> is validated against.
    /// </summary>
    public IReadOnlyList<int> SequenceBuckets { get; init; } = [64, 128, 256, 512];
}
