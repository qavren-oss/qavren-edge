using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// The terminal fallback every other chunker delegates to (spec 8.2). Forward cut via
/// <see cref="IChunkTokenizer.IndexByTokenCount"/>, each candidate nudged backwards through a 15%
/// sentence look-back, every cut snapped to a grapheme-cluster boundary, and the next window
/// seeded by counting <c>OverlapTokens</c> back from the emitted chunk's end.
/// </summary>
public sealed class TokenWindowChunker : IChunker
{
    private readonly ILogger _logger;

    /// <summary>Creates the chunker with no logger.</summary>
    public TokenWindowChunker()
        : this(null)
    {
    }

    /// <summary>Creates the chunker, logging through <paramref name="logger"/> (or nowhere, when <see langword="null"/>).</summary>
    public TokenWindowChunker(ILogger? logger) => _logger = logger ?? NullLogger.Instance;

    /// <inheritdoc/>
    public string Id => ChunkerIds.TokenWindow;

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public IEnumerable<ChunkDraft> Chunk(
        ExtractedDocument document, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenizer);

        return Windows(document.Text, options, tokenizer);
    }

    private IEnumerable<ChunkDraft> Windows(
        string text, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        var ordinal = 0;
        foreach (var window in TokenWindow.Windows(text, 0, text.Length, options, tokenizer, Id))
        {
            yield return ChunkAssembly.CreateSlice(
                Id,
                text,
                window.Start,
                window.End,
                window.TokenCount,
                [],
                options,
                tokenizer,
                _logger,
                ordinal++,
                DocumentBlockKind.Paragraph,
                page: -1);
        }
    }
}
