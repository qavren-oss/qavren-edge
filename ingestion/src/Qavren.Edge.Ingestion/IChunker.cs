namespace Qavren.Edge.Ingestion;

/// <summary>
/// One chunk before identity and embedding. <see cref="HeadingPath"/> is the STRUCTURAL array; the
/// stored column is the joined breadcrumb (spec 8.3).
/// </summary>
public sealed record ChunkDraft(
    int Ordinal,
    string Text,
    string EmbedText,
    IReadOnlyList<string> HeadingPath,
    string? Breadcrumb,
    int CharStart,
    int CharEnd,
    int TokenCount,
    DocumentBlockKind Kind,
    int Page);

/// <summary>A chunker. Synchronous by design: CPU over an in-memory string, no I/O (spec 8.2).</summary>
public interface IChunker
{
    /// <summary>One of <see cref="ChunkerIds"/>.</summary>
    string Id { get; }

    /// <summary>Bumped when the emitted boundaries change; part of the recipe hash.</summary>
    int Version { get; }

    /// <summary>Chunk one extracted document against a frozen budget.</summary>
    IEnumerable<ChunkDraft> Chunk(
        ExtractedDocument document, ResolvedChunkOptions options, IChunkTokenizer tokenizer);
}

/// <summary>The four chunker ids SP3 ships.</summary>
public static class ChunkerIds
{
    public const string Auto = "auto";
    public const string Plain = "plain";
    public const string MarkdownHeading = "markdown-heading";
    public const string TokenWindow = "token-window";
}
