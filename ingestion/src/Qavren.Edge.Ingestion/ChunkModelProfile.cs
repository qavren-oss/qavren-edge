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
    string? QueryPrefix = null);
