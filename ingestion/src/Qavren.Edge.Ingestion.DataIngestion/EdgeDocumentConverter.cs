using System.Globalization;
using System.Text;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Qavren.Edge.Ingestion.DataIngestion;

/// <summary>
/// Converts between SP3's <see cref="ExtractedDocument"/> and MEDI's <see cref="IngestionDocument"/>,
/// in both directions (spec 11, plan task 6.3 step 1).
/// <para>
/// <b>Lossless in both directions, except for two things, both by design.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Spans.</b> MEDI's model has no character offsets: an element is its text. <see cref="ToMedi"/>
/// slices each block out of <see cref="ExtractedDocument.Text"/> and <see cref="FromMedi"/> rebuilds
/// a text buffer by joining the element texts with a blank line, assigning fresh <c>[Start, End)</c>
/// offsets into <i>that</i> buffer. Every block's text, kind, heading level and page number survive
/// the round trip; the original offsets do not.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Images.</b> <see cref="DocumentBlockKind"/> has no image kind (spec 6, suite decision 9), so
/// an <see cref="IngestionDocumentImage"/> converts to nothing. <see cref="FromMedi"/> logs event
/// <see cref="EdgeIngestionEventIds.MediImagesDropped"/> (917) <b>once per document</b>, with the
/// count, never once per image.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>What MEDI's vocabulary actually is.</b> Spec 6 describes <see cref="DocumentBlockKind"/> as
/// "MEDI's vocabulary". The shipped abstractions carry five concrete element types — paragraph,
/// header, footer, table and image — so <see cref="DocumentBlockKind.Heading"/>,
/// <see cref="DocumentBlockKind.Footer"/> and <see cref="DocumentBlockKind.Paragraph"/> map to a
/// native element, a run of <see cref="DocumentBlockKind.TableRow"/> blocks becomes one
/// <see cref="IngestionDocumentTable"/> (one row per block, one column), and the four kinds MEDI has no
/// element for — <see cref="DocumentBlockKind.ListItem"/>, <see cref="DocumentBlockKind.Code"/>,
/// <see cref="DocumentBlockKind.Caption"/>, <see cref="DocumentBlockKind.Quote"/> — ride on an
/// <see cref="IngestionDocumentParagraph"/> carrying <see cref="BlockKindKey"/> in its metadata. The
/// mapping is one switch over all eight members with no default bucket, so a ninth kind fails to
/// compile here rather than silently becoming a paragraph.
/// </para>
/// <para>
/// Document-level facts (extractor id and version, media type, page count, text-layer flag,
/// warnings and <see cref="ExtractedDocument.Metadata"/>) have no home on <see cref="IngestionDocument"/>
/// either, so they travel on the root <see cref="IngestionDocumentSection"/>'s metadata under the
/// <c>qedge.</c> keys declared below. A MEDI document that did not come from SP3 simply has none of
/// them and gets the documented defaults.
/// </para>
/// </summary>
public static class EdgeDocumentConverter
{
    /// <summary>Root-section metadata: <see cref="ExtractedDocument.ExtractorId"/>.</summary>
    public const string ExtractorIdKey = "qedge.extractorId";

    /// <summary>Root-section metadata: <see cref="ExtractedDocument.ExtractorVersion"/>.</summary>
    public const string ExtractorVersionKey = "qedge.extractorVersion";

    /// <summary>Root-section metadata: <see cref="ExtractedDocument.MediaType"/>.</summary>
    public const string MediaTypeKey = "qedge.mediaType";

    /// <summary>Root-section metadata: <see cref="ExtractedDocument.PageCount"/>. Absent when null.</summary>
    public const string PageCountKey = "qedge.pageCount";

    /// <summary>Root-section metadata: <see cref="ExtractedDocument.HasTextLayer"/>.</summary>
    public const string HasTextLayerKey = "qedge.hasTextLayer";

    /// <summary>
    /// Root-section metadata: <see cref="ExtractedDocument.Warnings"/>, carried as the list object
    /// itself. Absent when null.
    /// </summary>
    public const string WarningsKey = "qedge.warnings";

    /// <summary>
    /// Root-section metadata prefix: every <see cref="ExtractedDocument.Metadata"/> entry is stored as
    /// <c>qedge.meta.&lt;key&gt;</c>.
    /// </summary>
    public const string DocumentMetadataPrefix = "qedge.meta.";

    /// <summary>
    /// Element metadata: the <see cref="DocumentBlockKind"/> name, on the four kinds MEDI has no
    /// element type for. Absent on every element whose type already says what it is.
    /// </summary>
    public const string BlockKindKey = "qedge.blockKind";

    /// <summary>The separator <see cref="FromMedi"/> places between two element texts.</summary>
    public const string BlockSeparator = "\n\n";

    /// <summary>The separator <see cref="FromMedi"/> places between two cells of one table row.</summary>
    public const string CellSeparator = " | ";

    /// <summary>The extractor id a MEDI document that carries no SP3 facts reports.</summary>
    public const string DefaultExtractorId = "medi";

    /// <summary>The media type a MEDI document that carries no SP3 facts reports.</summary>
    public const string DefaultMediaType = "application/octet-stream";

    private static readonly Action<ILogger, string, int, Exception?> LogImagesDropped =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(EdgeIngestionEventIds.MediImagesDropped, nameof(EdgeIngestionEventIds.MediImagesDropped)),
            "Document '{DocumentId}': {Count} MEDI image element(s) dropped; SP3's document model has no image kind.");

    /// <summary>
    /// SP3 to MEDI. One root section holds the document facts and one element per block, in block
    /// order. Zero-length blocks are skipped, because a MEDI element cannot be empty.
    /// </summary>
    /// <param name="document">The extracted document.</param>
    /// <returns>The MEDI document.</returns>
    public static IngestionDocument ToMedi(ExtractedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = new IngestionDocumentSection();
        root.Metadata[ExtractorIdKey] = document.ExtractorId;
        root.Metadata[ExtractorVersionKey] = document.ExtractorVersion;
        root.Metadata[MediaTypeKey] = document.MediaType;
        root.Metadata[HasTextLayerKey] = document.HasTextLayer;
        if (document.PageCount is { } pageCount)
        {
            root.Metadata[PageCountKey] = pageCount;
        }

        if (document.Warnings is { } warnings)
        {
            root.Metadata[WarningsKey] = warnings;
        }

        foreach (var (key, value) in document.Metadata)
        {
            root.Metadata[DocumentMetadataPrefix + key] = value;
        }

        var text = document.Text;
        var blocks = document.Blocks;
        for (var i = 0; i < blocks.Count;)
        {
            var block = blocks[i];
            if (block.Kind == DocumentBlockKind.TableRow)
            {
                // A run of consecutive rows is one table: rows x 1, each cell the row's verbatim text.
                var rows = new List<DocumentBlock>();
                while (i < blocks.Count && blocks[i].Kind == DocumentBlockKind.TableRow)
                {
                    if (blocks[i].End > blocks[i].Start)
                    {
                        rows.Add(blocks[i]);
                    }

                    i++;
                }

                if (rows.Count != 0)
                {
                    root.Elements.Add(BuildTable(text, rows));
                }

                continue;
            }

            i++;
            if (block.End <= block.Start)
            {
                continue;
            }

            root.Elements.Add(BuildElement(text[block.Start..block.End], block));
        }

        var medi = new IngestionDocument(document.DocumentId);
        medi.Sections.Add(root);
        return medi;
    }

    /// <summary>
    /// MEDI to SP3. Walks every section in order, descending into nested sections, and rebuilds one
    /// text buffer with fresh offsets. A table yields one <see cref="DocumentBlockKind.TableRow"/>
    /// per row, its cells joined with <see cref="CellSeparator"/>. Images are dropped and counted;
    /// a non-zero count is event 917, once.
    /// </summary>
    /// <param name="document">The MEDI document.</param>
    /// <param name="logger">Receives event 917. Null discards it.</param>
    /// <returns>The extracted document.</returns>
    public static ExtractedDocument FromMedi(IngestionDocument document, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var log = logger ?? NullLogger.Instance;
        var text = new StringBuilder();
        var blocks = new List<DocumentBlock>();
        var images = 0;

        foreach (var section in document.Sections)
        {
            Collect(section, text, blocks, ref images);
        }

        if (images != 0)
        {
            LogImagesDropped(log, document.Identifier, images, null);
        }

        var facts = document.Sections.Count != 0 && document.Sections[0].HasMetadata
            ? document.Sections[0].Metadata
            : null;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<IngestionFailure>? warnings = null;
        if (facts is not null)
        {
            foreach (var (key, value) in facts)
            {
                if (key.StartsWith(DocumentMetadataPrefix, StringComparison.Ordinal))
                {
                    metadata[key[DocumentMetadataPrefix.Length..]] = AsString(value);
                }
                else if (!key.StartsWith("qedge.", StringComparison.Ordinal) && value is string plain)
                {
                    // A reader's own string-valued facts are worth keeping; anything else has no
                    // string column to land in.
                    metadata[key] = plain;
                }
            }

            if (facts.TryGetValue(WarningsKey, out var stored) && stored is IReadOnlyList<IngestionFailure> list)
            {
                warnings = list;
            }
        }

        return new ExtractedDocument(
            document.Identifier,
            ReadString(facts, ExtractorIdKey) ?? DefaultExtractorId,
            ReadInt(facts, ExtractorVersionKey) ?? 1,
            ReadString(facts, MediaTypeKey) ?? DefaultMediaType,
            text.ToString(),
            blocks,
            metadata,
            ReadInt(facts, PageCountKey),
            ReadBool(facts, HasTextLayerKey) ?? true,
            warnings);
    }

    private static IngestionDocumentElement BuildElement(string slice, DocumentBlock block)
    {
        IngestionDocumentElement element = block.Kind switch
        {
            DocumentBlockKind.Paragraph => new IngestionDocumentParagraph(slice),
            DocumentBlockKind.Heading => new IngestionDocumentHeader(slice) { Level = block.HeadingLevel },
            DocumentBlockKind.Footer => new IngestionDocumentFooter(slice),
            DocumentBlockKind.ListItem => Tagged(slice, DocumentBlockKind.ListItem),
            DocumentBlockKind.Code => Tagged(slice, DocumentBlockKind.Code),
            DocumentBlockKind.Caption => Tagged(slice, DocumentBlockKind.Caption),
            DocumentBlockKind.Quote => Tagged(slice, DocumentBlockKind.Quote),
            DocumentBlockKind.TableRow => throw new InvalidOperationException("Table rows are grouped by the caller."),

            // Not a fallback bucket: every named member is above, and an integer outside the enum
            // is a caller bug, not a paragraph.
            _ => throw new ArgumentOutOfRangeException(nameof(block), block.Kind, "Unknown DocumentBlockKind."),
        };

        element.Text = slice;
        element.PageNumber = block.PageNumber;
        return element;
    }

    private static IngestionDocumentParagraph Tagged(string slice, DocumentBlockKind kind)
    {
        var paragraph = new IngestionDocumentParagraph(slice);
        paragraph.Metadata[BlockKindKey] = kind.ToString();
        return paragraph;
    }

    private static IngestionDocumentTable BuildTable(string text, List<DocumentBlock> rows)
    {
        var cells = new IngestionDocumentElement[rows.Count, 1];
        var markdown = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            var slice = text[rows[r].Start..rows[r].End];
            cells[r, 0] = new IngestionDocumentParagraph(slice) { Text = slice, PageNumber = rows[r].PageNumber };
            if (r != 0)
            {
                markdown.Append('\n');
            }

            markdown.Append(slice);
        }

        var rendered = markdown.ToString();
        return new IngestionDocumentTable(rendered, cells) { Text = rendered, PageNumber = rows[0].PageNumber };
    }

    private static void Collect(
        IngestionDocumentElement element, StringBuilder text, List<DocumentBlock> blocks, ref int images)
    {
        switch (element)
        {
            case IngestionDocumentSection section:
                foreach (var child in section.Elements)
                {
                    Collect(child, text, blocks, ref images);
                }

                return;

            case IngestionDocumentImage:
                images++;
                return;

            case IngestionDocumentTable table:
                var cells = table.Cells;
                var rowCount = cells.GetLength(0);
                var columnCount = cells.GetLength(1);
                for (var r = 0; r < rowCount; r++)
                {
                    var row = new StringBuilder();
                    int? page = null;
                    for (var c = 0; c < columnCount; c++)
                    {
                        var cell = cells[r, c];
                        if (cell is null)
                        {
                            continue;
                        }

                        page ??= cell.PageNumber;
                        var content = TextOf(cell);
                        if (content.Length == 0)
                        {
                            continue;
                        }

                        if (row.Length != 0)
                        {
                            row.Append(CellSeparator);
                        }

                        row.Append(content);
                    }

                    Append(text, blocks, row.ToString(), DocumentBlockKind.TableRow, null, page ?? table.PageNumber);
                }

                return;

            case IngestionDocumentHeader header:
                Append(text, blocks, TextOf(header), DocumentBlockKind.Heading, header.Level, header.PageNumber);
                return;

            case IngestionDocumentFooter footer:
                Append(text, blocks, TextOf(footer), DocumentBlockKind.Footer, null, footer.PageNumber);
                return;

            default:
                var kind = DocumentBlockKind.Paragraph;
                if (element.HasMetadata
                    && element.Metadata.TryGetValue(BlockKindKey, out var tag)
                    && tag is string name
                    && Enum.TryParse<DocumentBlockKind>(name, ignoreCase: false, out var parsed))
                {
                    kind = parsed;
                }

                Append(text, blocks, TextOf(element), kind, null, element.PageNumber);
                return;
        }
    }

    private static void Append(
        StringBuilder text, List<DocumentBlock> blocks, string content, DocumentBlockKind kind, int? level, int? page)
    {
        if (content.Length == 0)
        {
            return;
        }

        if (text.Length != 0)
        {
            text.Append(BlockSeparator);
        }

        var start = text.Length;
        text.Append(content);
        blocks.Add(new DocumentBlock(kind, start, text.Length, level, page));
    }

    private static string TextOf(IngestionDocumentElement element)
    {
        var text = element.Text;
        return string.IsNullOrEmpty(text) ? element.GetMarkdown() : text;
    }

    private static string AsString(object? value) =>
        value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? ReadString(IDictionary<string, object?>? facts, string key) =>
        facts is not null && facts.TryGetValue(key, out var value) ? value as string : null;

    private static int? ReadInt(IDictionary<string, object?>? facts, string key) =>
        facts is not null && facts.TryGetValue(key, out var value) && value is int number ? number : null;

    private static bool? ReadBool(IDictionary<string, object?>? facts, string key) =>
        facts is not null && facts.TryGetValue(key, out var value) && value is bool flag ? flag : null;
}
