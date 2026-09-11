using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Qavren.Edge.Ingestion.Internal;
using MdTables = Markdig.Extensions.Tables;

namespace Qavren.Edge.Ingestion;

/// <summary>Knobs for <see cref="MarkdownExtractor"/> (spec 7.3).</summary>
public sealed class MarkdownExtractorOptions
{
    /// <summary>
    /// EMPTY by default, and that is a behaviour-changing default: no YAML front-matter key becomes
    /// a column out of the box. Every parsed key still reaches
    /// <see cref="ExtractedDocument.Metadata"/>; this list is the opt-in for promoting one.
    /// </summary>
    public IList<string> PromoteFrontMatterKeys { get; } = [];
}

/// <summary>
/// Spec 7.3's Markdown extractor, over Markdig. Blocks come from the top-level
/// <see cref="MarkdownDocument"/> children, and because every <c>MarkdownObject</c> carries a
/// <c>SourceSpan</c>, a block's <c>[Start, End)</c> is the VERBATIM source range — fences, tables
/// and links survive intact instead of being re-rendered.
/// </summary>
/// <remarks>
/// <para>
/// Spec 17 item 9's gate, resolved by measurement on Markdig 1.3.2: block spans index the string
/// handed to <c>Markdown.Parse</c>, character for character (<c>SourceSpan.End</c> is INCLUSIVE, so
/// the half-open end is <c>End + 1</c>). So this extractor normalises FIRST and parses the
/// normalised buffer — the first of the two branches the plan allows — and
/// <see cref="ExtractedDocument.Text"/> is exactly the string that was parsed. No re-parse is
/// needed and no offset is translated.
/// </para>
/// <para>
/// DEVIATION from spec 7.3 and plan step 6, recorded here until they are amended: blocks come from
/// the top-level children EXCEPT for <c>Table</c> and <c>ListBlock</c>, which are descended ONE
/// level so a table yields one <see cref="DocumentBlockKind.TableRow"/> per row and a list one
/// <see cref="DocumentBlockKind.ListItem"/> per top-level item. Taken literally, the top-level rule
/// leaves both of those kinds dead vocabulary and leaves Task 4.1's RepeatTableHeaderRow with no
/// granularity to repeat. Spans stay verbatim source ranges either way.
/// </para>
/// <para>
/// The pipeline is built explicitly and never with <c>UseAdvancedExtensions()</c>, which pulls in
/// roughly eighteen. Markdig earns its place by being a parser: a <c>## </c> line inside a fenced or
/// indented code block is a code line, a four-backtick fence containing three backticks is one
/// block, and raw HTML is one <c>HtmlBlock</c> rather than prose to be split.
/// </para>
/// </remarks>
public sealed class MarkdownExtractor : IDocumentExtractor
{
    private static readonly string[] ExtensionList = [".md", ".markdown"];
    private static readonly string[] MediaTypeList = [IngestionMediaTypes.Markdown];

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseYamlFrontMatter()
        .UsePreciseSourceLocation()
        .Build();

    private readonly MarkdownExtractorOptions _options;

    public MarkdownExtractor()
        : this(new MarkdownExtractorOptions())
    {
    }

    public MarkdownExtractor(MarkdownExtractorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string Id => "markdown";

    public int Version => 1;

    public IReadOnlyList<string> Extensions => ExtensionList;

    public IReadOnlyList<string> MediaTypes => MediaTypeList;

    public bool CanExtract(DocumentSourceItem item) => item is not null;

    public async ValueTask<ExtractedDocument> ExtractAsync(
        DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);

        var stream = await SourceStream
            .OpenSeekableAsync(item, context.Options, context.SourceId, cancellationToken)
            .ConfigureAwait(false);

        string raw;
        await using (stream.ConfigureAwait(false))
        {
            (raw, _, _) = await SourceStream
                .ReadTextAsync(stream, strictUtf8: false, item, context.SourceId, cancellationToken)
                .ConfigureAwait(false);
        }

        // Normalise BEFORE the parse, so the spans Markdig hands back already index Text.
        var text = context.Options.NormalizeText ? TextNormalizer.Normalize(raw) : raw;

        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(text, Pipeline);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new EdgeExtractionException(
                EdgeErrorCode.MarkdownParseFailed,
                $"Markdig could not parse '{item.DocumentId}'.",
                ex)
            {
                SourceId = context.SourceId,
                DocumentId = item.DocumentId,
                ExtractorName = Id,
                Remediation = "Check the document for a malformed construct, or register a different extractor for it.",
            };
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var blocks = new List<DocumentBlock>();

        foreach (var block in document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Collect(block, text, metadata, blocks);
        }

        return new ExtractedDocument(
            item.DocumentId,
            Id,
            Version,
            string.IsNullOrEmpty(item.MediaType) ? IngestionMediaTypes.Markdown : item.MediaType,
            text,
            blocks,
            metadata);
    }

    private static void Collect(
        Block block, string text, Dictionary<string, string> metadata, List<DocumentBlock> blocks)
    {
        switch (block)
        {
            case YamlFrontMatterBlock frontMatter:
                ParseFrontMatter(Slice(frontMatter, text), metadata);
                return;

            case HeadingBlock heading:
                Add(blocks, DocumentBlockKind.Heading, heading, text, heading.Level);
                return;

            case ParagraphBlock paragraph:
                Add(blocks, DocumentBlockKind.Paragraph, paragraph, text);
                return;

            case CodeBlock code:
                // Covers FencedCodeBlock and the indented form alike.
                Add(blocks, DocumentBlockKind.Code, code, text);
                return;

            case QuoteBlock quote:
                Add(blocks, DocumentBlockKind.Quote, quote, text);
                return;

            case MdTables.Table table:
                // One block per ROW, so DocumentBlockKind.TableRow is honest and a chunker that
                // repeats the header row has the granularity to do it.
                foreach (var row in table)
                {
                    if (row is MdTables.TableRow tableRow)
                    {
                        Add(blocks, DocumentBlockKind.TableRow, tableRow, text);
                    }
                }

                return;

            case ListBlock list:
                foreach (var listItem in list)
                {
                    if (listItem is ListItemBlock item)
                    {
                        Add(blocks, DocumentBlockKind.ListItem, item, text);
                    }
                }

                return;

            case HtmlBlock html:
                // Spec 7.3: one HtmlBlock is one block, not prose to be split.
                Add(blocks, DocumentBlockKind.Paragraph, html, text);
                return;

            case ThematicBreakBlock:
            case LinkReferenceDefinitionGroup:
            case LinkReferenceDefinition:
                // No text content worth a chunk.
                return;

            default:
                Add(blocks, DocumentBlockKind.Paragraph, block, text);
                return;
        }
    }

    private static void Add(
        List<DocumentBlock> blocks, DocumentBlockKind kind, MarkdownObject block, string text, int? headingLevel = null)
    {
        var (start, end) = Bounds(block, text);
        if (end > start)
        {
            blocks.Add(new DocumentBlock(kind, start, end, headingLevel));
        }
    }

    private static (int Start, int End) Bounds(MarkdownObject block, string text)
    {
        var span = block.Span;
        var start = Math.Clamp(span.Start, 0, text.Length);

        // Markdig's SourceSpan.End is INCLUSIVE; DocumentBlock's is half-open.
        var end = Math.Clamp(span.End + 1, start, text.Length);
        return (start, end);
    }

    private static string Slice(MarkdownObject block, string text)
    {
        var (start, end) = Bounds(block, text);
        return text[start..end];
    }

    /// <summary>
    /// A deliberately small front-matter reader: <c>key: value</c> pairs at the top level, values
    /// kept verbatim (a flow sequence stays the literal <c>[a, b]</c>). SP3 takes no YAML
    /// dependency for this — nothing downstream interprets a value, and a parser that is not there
    /// cannot be a trim or AOT problem.
    /// </summary>
    private static void ParseFrontMatter(string block, Dictionary<string, string> metadata)
    {
        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line is "---" or "..." || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length != 0)
            {
                metadata[key] = value;
            }
        }
    }
}
