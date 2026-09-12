using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// Spec 8.2's block-accumulating chunker: blocks are accumulated while they fit, a block over
/// budget goes to the token window, and a run under <c>MinTokens</c> merges forward. Offsets are
/// the accumulated blocks' <c>[first.Start, last.End)</c>, so the stored text is a verbatim slice
/// of <see cref="ExtractedDocument.Text"/> — the separating newlines included.
/// </summary>
/// <remarks>
/// Spec 8.2 words the accumulation over <see cref="DocumentBlockKind.Paragraph"/> blocks, which is
/// every block <see cref="PlainTextExtractor"/> emits. This implementation accumulates blocks of
/// EVERY kind, because <c>auto</c> resolves here for any non-Markdown media type and a chunker that
/// silently dropped a <c>TableRow</c> would lose the document rather than chunk it. The emitted
/// chunk takes the kind of the first block in the run.
/// </remarks>
public sealed class PlainChunker : IChunker
{
    private readonly ILogger _logger;

    public PlainChunker()
        : this(null)
    {
    }

    public PlainChunker(ILogger? logger) => _logger = logger ?? NullLogger.Instance;

    public string Id => ChunkerIds.Plain;

    public int Version => 1;

    public IEnumerable<ChunkDraft> Chunk(
        ExtractedDocument document, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenizer);

        return Accumulate(document, options, tokenizer);
    }

    private IEnumerable<ChunkDraft> Accumulate(
        ExtractedDocument document, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        var text = document.Text;
        var ordinal = 0;
        var pendingStart = -1;
        var pendingEnd = -1;
        var pendingTokens = 0;
        var pendingKind = DocumentBlockKind.Paragraph;
        var pendingPage = -1;

        foreach (var block in document.Blocks)
        {
            if (block.End <= block.Start || TextSpans.IsWhiteSpace(text, block.Start, block.End))
            {
                continue;
            }

            var blockTokens = tokenizer.CountTokens(text.AsSpan(block.Start, block.End - block.Start));

            if (blockTokens > options.MaxTokens)
            {
                if (pendingStart >= 0)
                {
                    yield return Emit(
                        text, pendingStart, pendingEnd, pendingTokens, options, tokenizer,
                        ordinal++, pendingKind, pendingPage);
                    pendingStart = -1;
                }

                foreach (var draft in Overflow(text, block, blockTokens, options, tokenizer, ordinal))
                {
                    ordinal++;
                    yield return draft;
                }

                continue;
            }

            if (pendingStart < 0)
            {
                pendingStart = block.Start;
                pendingEnd = block.End;
                pendingTokens = blockTokens;
                pendingKind = block.Kind;
                pendingPage = block.PageNumber ?? -1;
                continue;
            }

            var combined = tokenizer.CountTokens(text.AsSpan(pendingStart, block.End - pendingStart));
            if (combined <= options.MaxTokens)
            {
                pendingEnd = block.End;
                pendingTokens = combined;
                continue;
            }

            yield return Emit(
                text, pendingStart, pendingEnd, pendingTokens, options, tokenizer,
                ordinal++, pendingKind, pendingPage);

            pendingStart = block.Start;
            pendingEnd = block.End;
            pendingTokens = blockTokens;
            pendingKind = block.Kind;
            pendingPage = block.PageNumber ?? -1;
        }

        if (pendingStart >= 0)
        {
            yield return Emit(
                text, pendingStart, pendingEnd, pendingTokens, options, tokenizer,
                ordinal, pendingKind, pendingPage);
        }
    }

    private IEnumerable<ChunkDraft> Overflow(
        string text,
        DocumentBlock block,
        int blockTokens,
        ResolvedChunkOptions options,
        IChunkTokenizer tokenizer,
        int firstOrdinal)
    {
        if (options.Overflow == ChunkOverflow.Throw)
        {
            throw ChunkerFactory.ContextTooLong(Id, blockTokens, options.MaxTokens);
        }

        var ordinal = firstOrdinal;
        foreach (var window in TokenWindow.Windows(text, block.Start, block.End, options, tokenizer, Id))
        {
            yield return ChunkAssembly.CreateSlice(
                Id, text, window.Start, window.End, window.TokenCount, [], options, tokenizer,
                _logger, ordinal++, block.Kind, block.PageNumber ?? -1);

            if (options.Overflow == ChunkOverflow.Truncate)
            {
                // Cut to budget, and SAY SO (event 921, spec 8.2). This block only reaches here
                // because it costs more than MaxTokens, so every window after the first one is
                // content leaving the corpus: dropping it silently is the defect.
                ChunkAssembly.ReportTruncated(_logger, Id, blockTokens, options.MaxTokens);
                yield break;
            }
        }
    }

    private ChunkDraft Emit(
        string text,
        int start,
        int end,
        int tokens,
        ResolvedChunkOptions options,
        IChunkTokenizer tokenizer,
        int ordinal,
        DocumentBlockKind kind,
        int page) =>
        ChunkAssembly.CreateSlice(
            Id, text, start, end, tokens, [], options, tokenizer, _logger, ordinal, kind, page);
}
