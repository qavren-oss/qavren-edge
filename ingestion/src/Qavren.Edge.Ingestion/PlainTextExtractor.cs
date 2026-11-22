using Microsoft.Extensions.Logging;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion;

/// <summary>Knobs for <see cref="PlainTextExtractor"/> (spec 7.2).</summary>
public sealed class PlainTextExtractorOptions
{
    /// <summary>
    /// OFF by default, and that is a behaviour-changing default: out of the box, invalid UTF-8
    /// falls back to byte-preserving Latin-1 and is LOGGED as event 914, never thrown. Setting it
    /// raises <see cref="EdgeErrorCode.DocumentEncodingUndecodable"/> (6106) instead.
    /// </summary>
    public bool StrictUtf8 { get; set; }
}

/// <summary>
/// Spec 7.2's plain-text extractor. Blocks are <see cref="DocumentBlockKind.Paragraph"/>s split on
/// blank lines; encoding resolves BOM, then a strict UTF-8 probe, then Latin-1, with no
/// third-party dependency.
/// </summary>
public sealed class PlainTextExtractor : IDocumentExtractor
{
    private static readonly string[] ExtensionList = [".txt", ".log", ".csv", ".text"];
    private static readonly string[] MediaTypeList = [IngestionMediaTypes.PlainText];

    // CA1848: this repo runs latest-recommended with TreatWarningsAsErrors, which rejects the
    // ILogger extension methods. LoggerMessage.Define keeps the event id and the template.
    private static readonly Action<ILogger, string, Exception?> LogEncodingFallback =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(EdgeIngestionEventIds.EncodingFallback, nameof(EdgeIngestionEventIds.EncodingFallback)),
            "Document {DocumentId} is not valid UTF-8; decoded with the byte-preserving Latin-1 fallback.");

    private readonly PlainTextExtractorOptions _options;

    /// <summary>Creates the extractor with default <see cref="PlainTextExtractorOptions"/>.</summary>
    public PlainTextExtractor()
        : this(new PlainTextExtractorOptions())
    {
    }

    /// <summary>Creates the extractor with the given <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public PlainTextExtractor(PlainTextExtractorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public string Id => "text";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions => ExtensionList;

    /// <inheritdoc/>
    public IReadOnlyList<string> MediaTypes => MediaTypeList;

    /// <inheritdoc/>
    public bool CanExtract(DocumentSourceItem item) => item is not null;

    /// <inheritdoc/>
    public async ValueTask<ExtractedDocument> ExtractAsync(
        DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);

        var stream = await SourceStream
            .OpenSeekableAsync(item, context.Options, context.SourceId, cancellationToken)
            .ConfigureAwait(false);

        string raw;
        bool usedFallback;
        await using (stream.ConfigureAwait(false))
        {
            (raw, _, usedFallback) = await SourceStream
                .ReadTextAsync(stream, _options.StrictUtf8, item, context.SourceId, cancellationToken)
                .ConfigureAwait(false);
        }

        // Normalise BEFORE any offset is computed (spec 6). Off, this is the identity and the
        // offsets index the raw decode.
        var text = context.Options.NormalizeText ? TextNormalizer.Normalize(raw) : raw;

        // Spec 13.3 keeps 6106 unreachable on the defaults: the byte-preserving fallback is
        // reported as LOG event 914 and nothing else, so a consumer filtering ExtractedDocument
        // .Warnings on DocumentEncodingUndecodable only ever sees it under StrictUtf8, where it is
        // thrown. Attaching it here as a warning too would put the error code on the happy path.
        if (usedFallback)
        {
            LogEncodingFallback(context.Logger, item.DocumentId, null);
        }

        // Paragraphs are runs of non-blank lines; the separating newlines belong to no block.
        var blocks = new List<DocumentBlock>();
        ParagraphSplitter.Split(text, baseOffset: 0, pageNumber: null, blocks);

        return new ExtractedDocument(
            item.DocumentId,
            Id,
            Version,
            string.IsNullOrEmpty(item.MediaType) ? IngestionMediaTypes.PlainText : item.MediaType,
            text,
            blocks,
            new Dictionary<string, string>(StringComparer.Ordinal),
            PageCount: null,
            HasTextLayer: true);
    }
}
