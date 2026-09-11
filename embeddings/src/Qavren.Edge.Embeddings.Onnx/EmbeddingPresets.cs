namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>
/// The four pinned presets. Only half of this catalogue is hand-written: every
/// <c>OnnxModelManifest</c> - repo, full commit SHA, per-file path, role, size and SHA-256, and
/// SPDX licence - lives in <c>EmbeddingPresets.g.cs</c>, generated from one
/// <c>paths-info</c> call per repo and committed a wave earlier. Nothing here retypes a digest.
/// </summary>
public static class EmbeddingPresets
{
    /// <summary>
    /// DEFAULT. all-MiniLM-L6-v2 int8, 23,026,053 B, 384-d, mean + L2, 256 tokens, no prefixes,
    /// Apache-2.0. Its <c>qint8_arm64</c> / <c>qint8_avx512</c> / <c>qint8_avx512_vnni</c> files
    /// are ONE blob under three names, so a single download covers every RID. 384 matches SP1's
    /// own <c>VecTable</c> example, so the sample needs no schema change.
    /// </summary>
    public static EmbeddingPreset MiniLmL6V2Int8 { get; } = new()
    {
        Id = "all-minilm-l6-v2-int8",
        Manifest = EmbeddingPresetManifests.MiniLmL6V2Int8,
        ModelFile = "onnx/model_qint8_arm64.onnx",
        TokenizerFile = "vocab.txt",
        TokenizerKind = EdgeTokenizerKind.WordPieceVocabTxt,
        LowerCase = true,
        Dimensions = 384,
        MaxSequenceLength = 256,
        Pooling = EmbeddingPooling.Mean,
    };

    /// <summary>all-MiniLM-L6-v2 fp32, 90,405,214 B. Same numbers, no quantization loss.</summary>
    public static EmbeddingPreset MiniLmL6V2Fp32 { get; } = new()
    {
        Id = "all-minilm-l6-v2-fp32",
        Manifest = EmbeddingPresetManifests.MiniLmL6V2Fp32,
        ModelFile = "onnx/model.onnx",
        TokenizerFile = "vocab.txt",
        TokenizerKind = EdgeTokenizerKind.WordPieceVocabTxt,
        LowerCase = true,
        Dimensions = 384,
        MaxSequenceLength = 256,
        Pooling = EmbeddingPooling.Mean,
    };

    /// <summary>
    /// bge-small-en-v1.5, 133,093,490 B fp32, 384-d, <b>CLS</b> pooling, 512 tokens, MIT. BAAI
    /// publishes no quantized ONNX. <see cref="EmbeddingPreset.QueryPrefix"/> is set and applied
    /// to queries only.
    /// </summary>
    public static EmbeddingPreset BgeSmallEnV15 { get; } = new()
    {
        Id = "bge-small-en-v1.5",
        Manifest = EmbeddingPresetManifests.BgeSmallEnV15,
        ModelFile = "onnx/model.onnx",
        TokenizerFile = "vocab.txt",
        TokenizerKind = EdgeTokenizerKind.WordPieceVocabTxt,
        LowerCase = true,
        Dimensions = 384,
        MaxSequenceLength = 512,
        Pooling = EmbeddingPooling.Cls,
        // SOURCE: BAAI's published retrieval instruction for the bge-*-en-v1.5 family, trailing
        // space included - the models were trained with it, and a changed or trimmed prefix is a
        // silent retrieval-quality regression rather than an error. It is asserted VERBATIM by
        // PresetCatalogueTests, and neither the spec nor the plan quotes the string, so it is
        // pinned by this line alone: OWNER CONFIRMATION OWED before release.
        QueryPrefix = "Represent this sentence for searching relevant passages: ",
    };

    /// <summary>
    /// OPT-IN. nomic-embed-text-v1.5 int8, 137,296,292 B, <b>768-d</b>, Apache-2.0, mandatory
    /// <c>search_query: </c> / <c>search_document: </c> prefixes. Doubles vec0 storage. Pooling is
    /// mean + <b><see cref="EmbeddingPreset.PostPoolLayerNorm"/> = true</b> + L2: this model's
    /// <c>modules.json</c> has no Normalize module and its reference pipeline inserts
    /// <c>F.layer_norm</c> over the 768 dim between pooling and L2. It is the only shipped preset
    /// that sets the flag, and the preset-catalogue table test guards it.
    /// </summary>
    public static EmbeddingPreset NomicEmbedTextV15Int8 { get; } = new()
    {
        Id = "nomic-embed-text-v1.5-int8",
        Manifest = EmbeddingPresetManifests.NomicEmbedTextV15Int8,
        ModelFile = "onnx/model_quantized.onnx",
        TokenizerFile = "vocab.txt",
        TokenizerKind = EdgeTokenizerKind.WordPieceVocabTxt,
        LowerCase = true,
        Dimensions = 768,
        // 512, NOT the 8192 nomic-embed-text-v1.5 is documented to support. This release's
        // SequenceBuckets ladder is [64, 128, 256, 512] (spec 7's default, which no preset
        // overrides), and the batch assembler rounds up to a bucket, so 512 is the longest
        // sequence anything here can actually emit. Raising this without adding buckets would
        // silently truncate at 512 anyway. Neither the spec nor the plan states a figure for this
        // preset: OWNER CONFIRMATION OWED on whether the ladder and this ceiling should grow.
        MaxSequenceLength = 512,
        Pooling = EmbeddingPooling.Mean,
        PostPoolLayerNorm = true,
        QueryPrefix = "search_query: ",
        DocumentPrefix = "search_document: ",
    };

    /// <summary>Every shipped preset, in catalogue order.</summary>
    public static IReadOnlyList<EmbeddingPreset> All { get; } =
        [MiniLmL6V2Int8, MiniLmL6V2Fp32, BgeSmallEnV15, NomicEmbedTextV15Int8];

    /// <summary>Resolves a preset by <see cref="EmbeddingPreset.Id"/>.</summary>
    /// <param name="id">The preset id.</param>
    /// <returns>The preset.</returns>
    /// <exception cref="EdgeEmbeddingException">
    /// <see cref="EdgeErrorCode.EmbeddingPresetNotFound"/> when no shipped preset carries that id.
    /// </exception>
    public static EmbeddingPreset ById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Id, id, StringComparison.Ordinal))
            {
                return All[i];
            }
        }

        throw new EdgeEmbeddingException(
            EdgeErrorCode.EmbeddingPresetNotFound,
            id,
            $"No shipped preset is named '{id}'. Known ids: {string.Join(", ", All.Select(p => p.Id))}.");
    }
}
