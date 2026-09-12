using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Qavren.Edge.Ingestion.DataIngestion;

/// <summary>
/// Wraps an SP3 <see cref="IChunker"/> behind MEDI's <see cref="IngestionChunker{T}"/> contract
/// (spec 11, plan task 6.3 step 2).
/// <para>
/// <b>It carries no constant.</b> MEDI's shipped chunkers default to 2000 tokens with a 500-token
/// overlap, four to eight times what a 384-dimension device model can encode; this adapter takes the
/// <see cref="ResolvedChunkOptions"/> the caller already froze — normally through
/// <see cref="ChunkOptions.Resolve"/> against the real <see cref="IChunkTokenizer"/> — and hands
/// that budget to the chunker verbatim.
/// </para>
/// <para>
/// Each <see cref="IngestionChunk{T}"/> carries the chunk's stored text as <c>Content</c>, its
/// breadcrumb as <c>Context</c>, and the rest of the <see cref="ChunkDraft"/> under the metadata
/// keys declared here, so <see cref="EdgeVectorStoreMediWriter"/> can write the same row SP3's own
/// runner would.
/// </para>
/// </summary>
public sealed class EdgeChunkerMediAdapter : IngestionChunker<string>
{
    /// <summary>Chunk metadata: <see cref="ChunkDraft.Ordinal"/>.</summary>
    public const string OrdinalKey = "qedge.ordinal";

    /// <summary>
    /// Chunk metadata: <see cref="ChunkDraft.EmbedText"/> — breadcrumb plus body, the text SP3
    /// embeds and hashes. <c>Content</c> is the stored text, never this.
    /// </summary>
    public const string EmbedTextKey = "qedge.embedText";

    /// <summary>Chunk metadata: <see cref="ChunkDraft.CharStart"/>.</summary>
    public const string CharStartKey = "qedge.charStart";

    /// <summary>Chunk metadata: <see cref="ChunkDraft.CharEnd"/>.</summary>
    public const string CharEndKey = "qedge.charEnd";

    /// <summary>Chunk metadata: <see cref="ChunkDraft.TokenCount"/>.</summary>
    public const string TokenCountKey = "qedge.tokenCount";

    /// <summary>Chunk metadata: <see cref="ChunkDraft.Kind"/>'s name.</summary>
    public const string BlockKindKey = EdgeDocumentConverter.BlockKindKey;

    /// <summary>Chunk metadata: <see cref="ChunkDraft.Page"/>, -1 when unknown.</summary>
    public const string PageKey = "qedge.page";

    private readonly IChunker _chunker;
    private readonly ResolvedChunkOptions _options;
    private readonly IChunkTokenizer _tokenizer;
    private readonly ILogger _logger;

    /// <summary>Creates the adapter.</summary>
    /// <param name="chunker">The SP3 chunker to run.</param>
    /// <param name="options">The frozen budget. Resolve it against <paramref name="tokenizer"/>.</param>
    /// <param name="tokenizer">The tokenizer the chunker counts with.</param>
    /// <param name="logger">Receives the chunker's and converter's events. Null discards them.</param>
    public EdgeChunkerMediAdapter(
        IChunker chunker, ResolvedChunkOptions options, IChunkTokenizer tokenizer, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenizer);

        _chunker = chunker;
        _options = options;
        _tokenizer = tokenizer;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The wrapped chunker.</summary>
    public IChunker Chunker => _chunker;

    /// <summary>The frozen budget every document is chunked against.</summary>
    public ResolvedChunkOptions Options => _options;

    /// <inheritdoc />
    public override IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IngestionDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        return Chunk(document, cancellationToken).ToAsyncEnumerable();
    }

    private IEnumerable<IngestionChunk<string>> Chunk(IngestionDocument document, CancellationToken cancellationToken)
    {
        var extracted = EdgeDocumentConverter.FromMedi(document, _logger);
        foreach (var draft in _chunker.Chunk(extracted, _options, _tokenizer))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = new IngestionChunk<string>(draft.Text, document, draft.Breadcrumb);
            chunk.Metadata[OrdinalKey] = draft.Ordinal;
            chunk.Metadata[EmbedTextKey] = draft.EmbedText;
            chunk.Metadata[CharStartKey] = draft.CharStart;
            chunk.Metadata[CharEndKey] = draft.CharEnd;
            chunk.Metadata[TokenCountKey] = draft.TokenCount;
            chunk.Metadata[BlockKindKey] = draft.Kind.ToString();
            chunk.Metadata[PageKey] = draft.Page;
            yield return chunk;
        }
    }
}
