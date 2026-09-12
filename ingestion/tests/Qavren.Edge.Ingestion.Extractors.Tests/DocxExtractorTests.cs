using Qavren.Edge.Ingestion.OpenXml;
using Xunit;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// One test per committed tree, each asserting the exact output plan Task 1.3 Step 5 states for it
/// — never merely "it did not throw".
/// </summary>
public class DocxExtractorTests
{
    [Fact]
    public async Task HeadingsResolveFromTheParagraphsOutlineLevel()
    {
        // styles.xml is present and names Heading1, but no paragraph references it: the levels come
        // from w:outlineLvl alone, so a style-name path would resolve nothing here.
        var (document, _) = await ExtractAsync("headings");

        Assert.Equal(6, document.Blocks.Count);
        Assert.Equal(
            [
                (DocumentBlockKind.Heading, 1, "Top level heading"),
                (DocumentBlockKind.Paragraph, (int?)null, "Body under the h1."),
                (DocumentBlockKind.Heading, 2, "Second level heading"),
                (DocumentBlockKind.Paragraph, null, "Body under the h2."),
                (DocumentBlockKind.Heading, 3, "Third level heading"),
                (DocumentBlockKind.Paragraph, null, "Body under the h3."),
            ],
            Shape(document));
    }

    [Fact]
    public async Task RunsSplitByRsidNoiseAreOneBlockWithTheWholeSentence()
    {
        var (document, _) = await ExtractAsync("run-split");

        var block = Assert.Single(document.Blocks);
        Assert.Equal(DocumentBlockKind.Paragraph, block.Kind);
        Assert.Equal("One sentence split across five separate runs with rsid noise.", TestHarness.Slice(document, block));
        Assert.Equal("One sentence split across five separate runs with rsid noise.", document.Text);

        // The plan's prose says "60 characters"; the sentence it spells out is 61. The equality
        // above is the assertion that matters - whole string, never Contains.
        Assert.Equal(61, document.Text.Length);
    }

    [Fact]
    public async Task TableRowsArePipeJoinedHeaderFirstAndTheTrailingParagraphIsItsOwnBlock()
    {
        var (document, _) = await ExtractAsync("table");

        Assert.Equal(
            [
                (DocumentBlockKind.TableRow, (int?)null, "Region | Total"),
                (DocumentBlockKind.TableRow, null, "North | 12"),
                (DocumentBlockKind.TableRow, null, "South | 34"),
                (DocumentBlockKind.Paragraph, null, "A paragraph after the table."),
            ],
            Shape(document));
    }

    [Fact]
    public async Task TablesCanBeExcluded()
    {
        var (document, _) = await ExtractAsync("table", o => o.IncludeTables = false);

        var block = Assert.Single(document.Blocks);
        Assert.Equal("A paragraph after the table.", TestHarness.Slice(document, block));
    }

    [Fact]
    public async Task NumberedParagraphsAreListItemsAndTheNumberingTextIsNotRendered()
    {
        var (document, _) = await ExtractAsync("numbered-list");

        Assert.Equal(
            [
                (DocumentBlockKind.ListItem, (int?)null, "First list item."),
                (DocumentBlockKind.ListItem, null, "Second list item."),
                (DocumentBlockKind.Paragraph, null, "A plain paragraph that is not a list item."),
            ],
            Shape(document));
        Assert.DoesNotContain("1. ", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FootnotesAreAppendedAfterTheBodyAsFooterBlocks()
    {
        var (document, _) = await ExtractAsync("footnotes");

        Assert.Equal(
            [
                (DocumentBlockKind.Paragraph, (int?)null, "Body text with a note reference."),
                (DocumentBlockKind.Footer, null, "The footnote body text."),
            ],
            Shape(document));
    }

    [Fact]
    public async Task FootnotesAreDroppedWithIncludeNotesOff()
    {
        var (document, _) = await ExtractAsync("footnotes", o => o.IncludeNotes = false);

        var block = Assert.Single(document.Blocks);
        Assert.Equal("Body text with a note reference.", TestHarness.Slice(document, block));
        Assert.DoesNotContain("footnote body", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadersAndFootersAreExcludedByDefault()
    {
        var (document, _) = await ExtractAsync("header-footer");

        var block = Assert.Single(document.Blocks);
        Assert.Equal("The only body paragraph.", TestHarness.Slice(document, block));
        Assert.DoesNotContain("RUNNING HEADER", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RUNNING FOOTER", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadersAndFootersComeBackHeaderFirstFooterLastWhenAskedFor()
    {
        var (document, _) = await ExtractAsync("header-footer", o => o.ExcludeHeadersAndFooters = false);

        Assert.Equal(
            [
                (DocumentBlockKind.Footer, (int?)null, "RUNNING HEADER THAT MUST NOT BE INDEXED"),
                (DocumentBlockKind.Paragraph, null, "The only body paragraph."),
                (DocumentBlockKind.Footer, null, "RUNNING FOOTER THAT MUST NOT BE INDEXED"),
            ],
            Shape(document));
    }

    [Fact]
    public async Task HyperlinkTextSurvivesAndTheTargetDoesNot()
    {
        var (document, _) = await ExtractAsync("hyperlink");

        var block = Assert.Single(document.Blocks);
        Assert.Equal("See the linked phrase for details.", TestHarness.Slice(document, block));
        Assert.Equal("See the linked phrase for details.", document.Text);
        Assert.DoesNotContain("example.invalid", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextBoxContentIsReachedByDefault()
    {
        var (document, _) = await ExtractAsync("textbox");

        Assert.Equal(
            [
                (DocumentBlockKind.Paragraph, (int?)null, "Body paragraph outside the text box."),
                (DocumentBlockKind.Paragraph, null, "Text inside a w:txbxContent box."),
            ],
            Shape(document));
    }

    [Fact]
    public async Task TextBoxContentIsSkippedWithIncludeTextBoxesOff()
    {
        var (document, _) = await ExtractAsync("textbox", o => o.IncludeTextBoxes = false);

        var block = Assert.Single(document.Blocks);
        Assert.Equal("Body paragraph outside the text box.", TestHarness.Slice(document, block));
    }

    [Fact]
    public async Task AnEmptyBodyIsAnEmptyDocumentWithATextLayerAndNoException()
    {
        // The self-closing <w:body /> is the point: nothing to dereference. HasTextLayer stays
        // true because a DOCX is not a scan; the runner records it Indexed with zero chunks rather
        // than Failed, which the pipeline suite asserts.
        var (document, _) = await ExtractAsync("empty-body");

        Assert.Equal(string.Empty, document.Text);
        Assert.Empty(document.Blocks);
        Assert.True(document.HasTextLayer);
        Assert.Equal("docx", document.ExtractorId);
        Assert.Equal(1, document.ExtractorVersion);
        Assert.Equal(IngestionMediaTypes.Docx, document.MediaType);
    }

    [Fact]
    public async Task AParagraphStyleAbsentFromTheStylesPartDegradesToAParagraph()
    {
        var (document, _) = await ExtractAsync("unknown-style");

        Assert.Equal(
            [
                (DocumentBlockKind.Paragraph, (int?)null, "A paragraph whose pStyle names a style that is not in styles.xml."),
                (DocumentBlockKind.Paragraph, null, "A second, ordinary paragraph."),
            ],
            Shape(document));
        Assert.All(document.Blocks, b => Assert.Null(b.HeadingLevel));
    }

    [Fact]
    public async Task LocalisedHeadingStylesResolveThroughTheStylesPartsOutlineLevel()
    {
        // Neither styleId nor name contains "Heading" and no paragraph carries w:outlineLvl: the
        // levels are reachable ONLY through the styles part. The Ü in styles.xml is also the check
        // that parts are read as UTF-8.
        var (document, _) = await ExtractAsync("localised-style");

        Assert.Equal(
            [
                (DocumentBlockKind.Heading, 1, "Titre de premier niveau"),
                (DocumentBlockKind.Paragraph, (int?)null, "Corps sous le titre."),
                (DocumentBlockKind.Heading, 2, "Zweite Ebene"),
                (DocumentBlockKind.Paragraph, null, "Text unter der zweiten Ebene."),
            ],
            Shape(document));
    }

    [Fact]
    public void TheExtractorSourceContainsNoHeadingRegex()
    {
        var root = RepoSources.Locate("Qavren.Edge.Ingestion.OpenXml");
        if (root is null)
        {
            Assert.Skip("The repository source tree is not present (device lane or published output); nothing to grep.");
            return;
        }

        var files = 0;
        var offenders = new List<string>();
        foreach (var file in RepoSources.SourceFiles(root))
        {
            files++;
            foreach (var line in RepoSources.CodeLines(file))
            {
                if (line.Contains("Regex", StringComparison.Ordinal)
                    || line.Contains("\"Heading", StringComparison.Ordinal)
                    || line.Contains("StyleName", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.True(files > 0, "the grep is pointed at something real");
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task ANonZipIsSixOneZeroFour()
    {
        var item = FixtureCorpus.Item("not-a.docx", FixtureCorpus.Bytes("pdf/minimal-text.pdf"));
        var extractor = new DocxTextExtractor();

        var thrown = await Assert.ThrowsAsync<EdgeExtractionException>(() =>
            extractor.ExtractAsync(item, TestHarness.Context(new CapturingLogger()), CancellationToken.None).AsTask());

        Assert.Equal(EdgeErrorCode.DocumentMalformed, thrown.Code);
        Assert.Equal("docx", thrown.ExtractorName);
        Assert.Contains("not-a.docx", thrown.Message, StringComparison.Ordinal);
        Assert.NotNull(thrown.InnerException);
    }

    [Fact]
    public void TheDefaultsAreTheSpecsDefaults()
    {
        var options = new DocxExtractorOptions();

        Assert.True(options.ExcludeHeadersAndFooters);
        Assert.True(options.IncludeTextBoxes);
        Assert.True(options.IncludeNotes);
        Assert.True(options.IncludeTables);
        Assert.Equal(8L * 1024 * 1024, options.StreamingThresholdBytes);

        var extractor = new DocxTextExtractor();
        Assert.Equal("docx", extractor.Id);
        Assert.Equal(1, extractor.Version);
        Assert.Equal([".docx"], extractor.Extensions);
        Assert.Equal([IngestionMediaTypes.Docx], extractor.MediaTypes);
    }

    internal static async Task<(ExtractedDocument Document, CapturingLogger Logger)> ExtractAsync(
        string tree, Action<DocxExtractorOptions>? configure = null, CapturingLogger? logger = null)
    {
        logger ??= new CapturingLogger();
        var options = new DocxExtractorOptions();
        configure?.Invoke(options);
        var extractor = new DocxTextExtractor(options);
        var document = await extractor.ExtractAsync(DeterministicOpc.Item(tree), TestHarness.Context(logger), CancellationToken.None)
            .ConfigureAwait(false);
        return (document, logger);
    }

    /// <summary>Kind, heading level and verbatim slice for every block, in order.</summary>
    private static (DocumentBlockKind Kind, int? HeadingLevel, string Text)[] Shape(ExtractedDocument document) =>
        [.. document.Blocks.Select(b => (b.Kind, b.HeadingLevel, TestHarness.Slice(document, b)))];
}
