using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Ingestion.OpenXml.Internal;

namespace Qavren.Edge.Ingestion.OpenXml;

/// <summary>
/// Spec 7.5's DOCX extractor over DocumentFormat.OpenXml 3.5.1. Heading levels come from the
/// outline level — the paragraph's own <c>w:outlineLvl</c>, then the style's, through the styles
/// part — and never from a style name; runs are merged; tables are pipe-joined rows; headers and
/// footers are excluded by default; and above <see cref="DocxExtractorOptions.StreamingThresholdBytes"/>
/// the main part is read with <c>OpenXmlPartReader</c> instead of the DOM. <c>OpenXmlValidator</c>
/// is never called.
/// </summary>
public sealed class DocxTextExtractor : IDocumentExtractor
{
    private const string DomMode = "dom";
    private const string StreamingMode = "streaming";

    private static readonly string[] ExtensionList = [".docx"];
    private static readonly string[] MediaTypeList = [IngestionMediaTypes.Docx];

    // CA1848: the repo runs latest-recommended with TreatWarningsAsErrors, which rejects the ILogger
    // extension methods. LoggerMessage.Define keeps the event id and the template.
    private static readonly Action<ILogger, string, long, string, Exception?> LogExtractionMode =
        LoggerMessage.Define<string, long, string>(
            LogLevel.Debug,
            new EventId(EdgeIngestionEventIds.ExtractionStreamingMode, nameof(EdgeIngestionEventIds.ExtractionStreamingMode)),
            "Document {DocumentId} ({SizeBytes} bytes) is read in {Mode} mode.");

    private readonly DocxExtractorOptions _options;

    /// <summary>Creates the extractor with default <see cref="DocxExtractorOptions"/>.</summary>
    public DocxTextExtractor()
        : this(new DocxExtractorOptions())
    {
    }

    /// <summary>Creates the extractor with the given <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public DocxTextExtractor(DocxExtractorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public string Id => "docx";

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

        var stream = await SatelliteSourceStream
            .OpenSeekableAsync(item, context.Options, context.SourceId, cancellationToken)
            .ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            // System.IO.Packaging is synchronous end to end; there is nothing to await past the open.
            return Extract(stream, item, context, cancellationToken);
        }
    }

    private ExtractedDocument Extract(
        Stream stream, DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken)
    {
        var size = item.SizeBytes ?? stream.Length;
        var streaming = size > _options.StreamingThresholdBytes;
        LogExtractionMode(context.Logger, item.DocumentId, size, streaming ? StreamingMode : DomMode, null);

        var builder = new DocxTextBuilder(context.Options.NormalizeText);
        string? title = null;
        string? author = null;

        try
        {
            using var document = WordprocessingDocument.Open(stream, isEditable: false);
            var main = document.MainDocumentPart
                ?? throw new InvalidOperationException("The package has no main document part.");

            var styles = DocxStyleOutlineLevels.Load(main.StyleDefinitionsPart);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_options.ExcludeHeadersAndFooters)
            {
                foreach (var header in main.HeaderParts)
                {
                    if (header.Header is { } root)
                    {
                        DocxDomFeed.Feed(root, new DocxEventWalker(builder, _options, styles, DocumentBlockKind.Footer));
                    }
                }
            }

            var body = new DocxEventWalker(builder, _options, styles);
            if (streaming)
            {
                DocxPartReaderFeed.Feed(main, body);
            }
            else if (main.Document is { } root)
            {
                DocxDomFeed.Feed(root, body);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!_options.ExcludeHeadersAndFooters)
            {
                foreach (var footer in main.FooterParts)
                {
                    if (footer.Footer is { } root)
                    {
                        DocxDomFeed.Feed(root, new DocxEventWalker(builder, _options, styles, DocumentBlockKind.Footer));
                    }
                }
            }

            if (_options.IncludeNotes)
            {
                AppendNotes(main, builder, styles);
            }

            var properties = document.PackageProperties;
            title = properties.Title;
            author = properties.Creator;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not EdgeException)
        {
            throw new EdgeExtractionException(
                EdgeErrorCode.DocumentMalformed,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Document '{0}' is not a well-formed DOCX package ({1}).",
                    item.DocumentId,
                    ex.GetType().Name),
                ex)
            {
                ExtractorName = Id,
                SourceId = context.SourceId,
                DocumentId = item.DocumentId,
                Remediation =
                    "The document is recorded as failed and the run continues. Re-save it from Word or LibreOffice, or " +
                    "exclude it from the source's search pattern.",
            };
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(title))
        {
            metadata["title"] = title;
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            metadata["author"] = author;
        }

        // A DOCX with nothing in it is not a scan: there is nothing to recognise, which is not the
        // same as an unrecognised image. HasTextLayer stays true and the pipeline indexes zero chunks.
        return new ExtractedDocument(
            item.DocumentId,
            Id,
            Version,
            string.IsNullOrEmpty(item.MediaType) ? IngestionMediaTypes.Docx : item.MediaType,
            builder.Text,
            builder.Blocks,
            metadata,
            PageCount: null,
            HasTextLayer: true);
    }

    /// <summary>
    /// Footnotes then endnotes, each note's paragraphs as <see cref="DocumentBlockKind.Footer"/>
    /// blocks after the body — never spliced into the paragraph that references them. The
    /// separator and continuation-separator pseudo-notes are skipped.
    /// </summary>
    private void AppendNotes(MainDocumentPart main, DocxTextBuilder builder, IReadOnlyDictionary<string, int> styles)
    {
        if (main.FootnotesPart?.Footnotes is { } footnotes)
        {
            foreach (var note in footnotes.Elements<Footnote>())
            {
                if (note.Type is null)
                {
                    DocxDomFeed.Feed(note, new DocxEventWalker(builder, _options, styles, DocumentBlockKind.Footer));
                }
            }
        }

        if (main.EndnotesPart?.Endnotes is { } endnotes)
        {
            foreach (var note in endnotes.Elements<Endnote>())
            {
                if (note.Type is null)
                {
                    DocxDomFeed.Feed(note, new DocxEventWalker(builder, _options, styles, DocumentBlockKind.Footer));
                }
            }
        }
    }
}
