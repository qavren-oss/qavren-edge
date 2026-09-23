using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Qavren.Edge.Ingestion.Pdf;

/// <summary>
/// Spec 7.4's PDF extractor over PdfPig 0.1.16: page-at-a-time, stream-only, scanned-page aware.
/// </summary>
/// <remarks>
/// <para>
/// Four rules are load-bearing, and <c>Qavren.Edge.Ingestion.Extractors.Tests</c> asserts each:
/// the document is ALWAYS opened through <c>PdfDocument.Open(Stream, ParsingOptions)</c> — the
/// path overload reads the whole file into a byte array first; <c>GetPages()</c> is enumerated
/// lazily and never materialised, so one page's letters are live at a time;
/// <see cref="PdfExtractorOptions.SkipMissingFonts"/> defaults on; and
/// <see cref="PdfExtractorOptions.PageBudget"/> is a between-pages watchdog rather than a timeout.
/// </para>
/// <para>
/// <c>UglyToad.PdfPig.DocumentLayoutAnalysis.Export</c> is never referenced: its exporters use
/// <c>XmlSerializer</c> and are the package's only trim hazard.
/// </para>
/// </remarks>
public sealed partial class PdfTextExtractor : IDocumentExtractor
{
    private const string PageSeparator = "\n\n";

    private static readonly string[] ExtensionList = [".pdf"];
    private static readonly string[] MediaTypeList = [IngestionMediaTypes.Pdf];

    private static readonly ContentOrderTextExtractor.Options ContentOrderOptions = new()
    {
        SeparateParagraphsWithDoubleNewline = true,
        ReplaceWhitespaceWithSpace = true,
        NegativeGapAsWhitespace = true,
    };

    // CA1848: the repo runs latest-recommended with TreatWarningsAsErrors, which rejects the ILogger
    // extension methods. LoggerMessage.Define keeps the event id and the template.
    private static readonly Action<ILogger, string, int, int, double, Exception?> LogPageBudgetExceeded =
        LoggerMessage.Define<string, int, int, double>(
            LogLevel.Warning,
            new EventId(EdgeIngestionEventIds.PageTimedOut, nameof(EdgeIngestionEventIds.PageTimedOut)),
            "Document {DocumentId}: the cumulative page budget was passed after {PagesParsed} of {PageCount} pages " +
            "({ElapsedSeconds:F1} s); the remaining pages were not parsed.");

    private readonly PdfExtractorOptions _options;

    /// <summary>Creates the extractor with default <see cref="PdfExtractorOptions"/>.</summary>
    public PdfTextExtractor()
        : this(new PdfExtractorOptions())
    {
    }

    /// <summary>Creates the extractor with the given <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public PdfTextExtractor(PdfExtractorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public string Id => "pdf";

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
            // PdfPig is synchronous end to end; there is nothing to await past the open.
            return Extract(stream, item, context, cancellationToken);
        }
    }

    private ExtractedDocument Extract(
        Stream stream, DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken)
    {
        var parsing = new ParsingOptions
        {
            UseLenientParsing = _options.UseLenientParsing,
            SkipMissingFonts = _options.SkipMissingFonts,
            UseActualText = _options.UseActualText,
            MaxStackDepth = _options.MaxStackDepth,
            ClipPaths = false,
        };
        parsing.Passwords.AddRange(_options.Passwords);

        PdfDocument document;
        try
        {
            // The Stream overload wraps a seekable stream in StreamInputBytes and does not buffer.
            document = PdfDocument.Open(stream, parsing);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw Fault(
                EdgeErrorCode.DocumentEncrypted,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Document '{0}' is encrypted and none of the {1} configured password(s) opened it.",
                    item.DocumentId,
                    _options.Passwords.Count),
                ex,
                item,
                context,
                "Add the document's password to PdfExtractorOptions.Passwords, or exclude the file from the source.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw Malformed(item, context, ex, "PdfPig could not open it");
        }

        using (document)
        {
            var pageCount = document.NumberOfPages;
            var text = new StringBuilder();
            var blocks = new List<DocumentBlock>();
            var anyTextLayer = false;
            var pagesParsed = 0;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // GetPages() is lazy and stays lazy: no ToList(), one page's letters live at a time.
                foreach (var page in document.GetPages())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    AppendPage(page, text, blocks, context.Options.NormalizeText, ref anyTextLayer);
                    pagesParsed++;

                    // The between-pages watchdog (spec 7.4): cumulative, checked at the boundary,
                    // never mid-page. Every page already parsed is kept; the rest are not started.
                    if (pagesParsed < pageCount && stopwatch.Elapsed > _options.PageBudget)
                    {
                        LogPageBudgetExceeded(
                            context.Logger, item.DocumentId, pagesParsed, pageCount, stopwatch.Elapsed.TotalSeconds, null);

                        throw new EdgeExtractionException(
                            EdgeErrorCode.DocumentPageBudgetExceeded,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Document '{0}' passed the cumulative page budget of {1} after {2} of {3} pages; page {2} was " +
                                "the last one parsed.",
                                item.DocumentId,
                                _options.PageBudget,
                                pagesParsed,
                                pageCount))
                        {
                            ExtractorName = Id,
                            SourceId = context.SourceId,
                            DocumentId = item.DocumentId,
                            PageNumber = pagesParsed,
                            Remediation =
                                "Raise PdfExtractorOptions.PageBudget for this corpus, or split the document. The budget is " +
                                "checked between pages only; a single page that never returns cannot be interrupted in-process.",
                        };
                    }
                }
            }
            catch (EdgeIngestionException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                throw Malformed(
                    item,
                    context,
                    ex,
                    string.Format(CultureInfo.InvariantCulture, "PdfPig failed on page {0}", pagesParsed + 1));
            }

            return new ExtractedDocument(
                item.DocumentId,
                Id,
                Version,
                string.IsNullOrEmpty(item.MediaType) ? IngestionMediaTypes.Pdf : item.MediaType,
                text.ToString(),
                blocks,
                Metadata(document),
                PageCount: pageCount,
                HasTextLayer: anyTextLayer);
        }
    }

    private void AppendPage(
        Page page, StringBuilder text, List<DocumentBlock> blocks, bool normalize, ref bool anyTextLayer)
    {
        // Scanned pages are an outcome, not an error: no letters and at least one image means no
        // text layer on this page. A page with letters is a text-layer page whatever else it holds.
        if (page.Letters.Count == 0)
        {
            return;
        }

        anyTextLayer = true;

        var pageText = _options.ReadingOrder == PdfReadingOrderMode.Layout
            ? LayoutText(page)
            : ContentOrderTextExtractor.GetText(page, ContentOrderOptions);

        if (_options.JoinHyphenatedLineBreaks)
        {
            pageText = HyphenatedLineBreak().Replace(pageText, string.Empty);
        }

        if (normalize)
        {
            pageText = SatelliteTextNormalizer.Normalize(pageText);
        }

        if (pageText.Length == 0 || string.IsNullOrWhiteSpace(pageText))
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append(PageSeparator);
        }

        var baseOffset = text.Length;
        text.Append(pageText);
        SplitParagraphs(pageText, baseOffset, page.Number, blocks);
    }

    /// <summary>
    /// The layout pipeline, single-threaded: its options carry a <c>MaxDegreeOfParallelism</c> that
    /// defaults to every core, which nobody wants spinning up on a phone.
    /// </summary>
    private static string LayoutText(Page page)
    {
        var words = new NearestNeighbourWordExtractor(new NearestNeighbourWordExtractor.NearestNeighbourWordExtractorOptions { MaxDegreeOfParallelism = 1 })
            .GetWords(page.Letters);
        var blocks = new DocstrumBoundingBoxes(new DocstrumBoundingBoxes.DocstrumBoundingBoxesOptions { MaxDegreeOfParallelism = 1 })
            .GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);

        var builder = new StringBuilder();
        foreach (var block in ordered)
        {
            if (builder.Length > 0)
            {
                builder.Append(PageSeparator);
            }

            builder.Append(block.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Paragraph blocks split on blank lines, the same rule <c>PlainTextExtractor</c> applies, with
    /// the page number stamped on every block.
    /// </summary>
    private static void SplitParagraphs(string pageText, int baseOffset, int pageNumber, List<DocumentBlock> blocks)
    {
        var start = -1;
        var end = -1;
        var i = 0;

        while (i <= pageText.Length)
        {
            var lineEnd = pageText.IndexOf('\n', i);
            var hasNewline = lineEnd >= 0;
            var stop = hasNewline ? lineEnd : pageText.Length;

            if (i >= pageText.Length && !hasNewline && i > 0)
            {
                break;
            }

            if (IsBlank(pageText, i, stop))
            {
                if (start >= 0)
                {
                    blocks.Add(new DocumentBlock(DocumentBlockKind.Paragraph, baseOffset + start, baseOffset + end, null, pageNumber));
                    start = -1;
                }
            }
            else
            {
                if (start < 0)
                {
                    start = FirstNonWhitespace(pageText, i, stop);
                }

                end = LastNonWhitespaceExclusive(pageText, i, stop);
            }

            if (!hasNewline)
            {
                break;
            }

            i = lineEnd + 1;
        }

        if (start >= 0)
        {
            blocks.Add(new DocumentBlock(DocumentBlockKind.Paragraph, baseOffset + start, baseOffset + end, null, pageNumber));
        }
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int FirstNonWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return start;
    }

    private static int LastNonWhitespaceExclusive(string text, int start, int end)
    {
        for (var i = end - 1; i >= start; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return end;
    }

    private static Dictionary<string, string> Metadata(PdfDocument document)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var information = document.Information;
        if (!string.IsNullOrWhiteSpace(information?.Title))
        {
            metadata["title"] = information.Title;
        }

        if (!string.IsNullOrWhiteSpace(information?.Author))
        {
            metadata["author"] = information.Author;
        }

        return metadata;
    }

    private EdgeExtractionException Malformed(
        DocumentSourceItem item, ExtractionContext context, Exception inner, string what) =>
        Fault(
            EdgeErrorCode.DocumentMalformed,
            string.Format(
                CultureInfo.InvariantCulture,
                "Document '{0}' is not a well-formed PDF: {1} ({2}).",
                item.DocumentId,
                what,
                inner.GetType().Name),
            inner,
            item,
            context,
            "The document is recorded as failed and the run continues. Re-save it from its producer, or exclude it from " +
            "the source's search pattern.");

    private EdgeExtractionException Fault(
        EdgeErrorCode code,
        string message,
        Exception inner,
        DocumentSourceItem item,
        ExtractionContext context,
        string remediation) =>
        new(code, message, inner)
        {
            ExtractorName = Id,
            SourceId = context.SourceId,
            DocumentId = item.DocumentId,
            Remediation = remediation,
        };

    /// <summary>
    /// A letter, a hyphen, a line break, then a lower-case letter: the hyphen and the break go. The
    /// break is <c>\r?\n</c> because <c>ContentOrderTextExtractor</c> writes <c>Environment.NewLine</c>,
    /// and the join runs before spec 6's normalisation so it holds with <c>NormalizeText</c> off too.
    /// </summary>
    [GeneratedRegex(@"(?<=\p{L})-\r?\n(?=\p{Ll})")]
    private static partial Regex HyphenatedLineBreak();
}
