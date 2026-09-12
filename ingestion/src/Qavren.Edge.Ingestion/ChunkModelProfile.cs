namespace Qavren.Edge.Ingestion;

/// <summary>
/// The six facts about an embedding model that chunking and the recipe need (spec 11). Plain data,
/// declared HERE in the core, so no core type names <c>Qavren.Edge.Embeddings.Onnx</c>.
/// </summary>
public sealed record ChunkModelProfile(
    string Id,
    int Dimensions,
    int MaxSequenceLength,
    string Pooling,
    string? DocumentPrefix = null,
    string? QueryPrefix = null)
{
    /// <summary>
    /// Defaults matching SP2's preset of the same name, FIELD FOR FIELD. The ids are the
    /// lower-case strings SP2 declares, and the id is not cosmetic: it feeds
    /// <see cref="IngestionRecipe.ModelProfileId"/>, so a cased copy would give the ONNX-free and
    /// ONNX paths DIFFERENT recipe hashes for the same model.
    /// </summary>
    public static ChunkModelProfile MiniLmL6V2Int8 { get; } = new("all-minilm-l6-v2-int8", 384, 256, "Mean");

    /// <summary>The fp32 sibling of <see cref="MiniLmL6V2Int8"/>.</summary>
    public static ChunkModelProfile MiniLmL6V2Fp32 { get; } = new("all-minilm-l6-v2-fp32", 384, 256, "Mean");

    /// <summary>CLS pooling, a 512-token ceiling, and a query-side prefix only.</summary>
    public static ChunkModelProfile BgeSmallEnV15 { get; } = new(
        "bge-small-en-v1.5", 384, 512, "Cls",
        QueryPrefix: "Represent this sentence for searching relevant passages: ");

    /// <summary>
    /// BOTH prefixes, trailing spaces included, because SP2 declares both and this model requires
    /// them. <see cref="DocumentPrefix"/> here is a token RESERVE and a recipe input only — SP3
    /// never writes it into an embed text, because <c>OnnxEmbeddingGenerator.ApplyPrefix</c>
    /// already does (spec 8.3).
    /// </summary>
    public static ChunkModelProfile NomicEmbedTextV15Int8 { get; } = new(
        "nomic-embed-text-v1.5-int8", 768, 512, "Mean",
        DocumentPrefix: "search_document: ",
        QueryPrefix: "search_query: ");
}
