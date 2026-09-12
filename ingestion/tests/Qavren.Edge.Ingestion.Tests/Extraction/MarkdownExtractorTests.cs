using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// Spec 7.3 over the committed Markdown corpus. The three traps a line scanner gets wrong and a
/// parser gets right are each their own test, and every block is asserted as a verbatim source
/// slice - that is the claim the whole design rests on.
/// </summary>
public class MarkdownExtractorTests
{
    private static readonly string[] ExpectedExtensions = [".md", ".markdown"];
    private static readonly string[] ExpectedMediaTypes = [IngestionMediaTypes.Markdown];

    [Fact]
    public void TheExtractorDeclaresSpecSevenThreesIdentity()
    {
        var extractor = new MarkdownExtractor();

        Assert.Equal("markdown", extractor.Id);
        Assert.Equal(1, extractor.Version);
        Assert.Equal(ExpectedExtensions, extractor.Extensions);
        Assert.Equal(ExpectedMediaTypes, extractor.MediaTypes);

        // Spec 7.3's behaviour-changing default: no front-matter key is promoted out of the box.
        Assert.Empty(new MarkdownExtractorOptions().PromoteFrontMatterKeys);
    }

    [Theory]
    [InlineData("markdown/headings.md")]
    [InlineData("markdown/fences.md")]
    [InlineData("markdown/tables-lists.md")]
    [InlineData("markdown/raw-html.md")]
    [InlineData("markdown/giant-heading-section.md")]
    public async Task EveryBlockSpanIsAVerbatimSliceOfText(string fixture)
    {
        var document = await ExtractAsync(fixture);

        Assert.NotEmpty(document.Blocks);
        var previousEnd = 0;
        foreach (var block in document.Blocks)
        {
            Assert.True(block.Start >= 0);
            Assert.True(block.Start < block.End);
            Assert.True(block.End <= document.Text.Length);
            Assert.True(block.Start >= previousEnd, "blocks must not overlap");
            previousEnd = block.End;

            var slice = TestHarness.Slice(document, block);
            Assert.Equal(document.Text.Substring(block.Start, block.End - block.Start), slice);
            Assert.NotEqual(0, slice.Length);
        }
    }

    [Fact]
    public async Task YamlFrontMatterBecomesMetadataAndNoBlock()
    {
        var document = await ExtractAsync("markdown/headings.md");

        Assert.Equal("Heading fixture", document.Metadata["title"]);
        Assert.Equal("[alpha, beta]", document.Metadata["tags"]);
        Assert.DoesNotContain(
            document.Blocks,
            b => TestHarness.Slice(document, b).StartsWith("---", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePreambleBeforeTheFirstHeadingSurvives()
    {
        var document = await ExtractAsync("markdown/headings.md");

        var first = document.Blocks[0];
        Assert.Equal(DocumentBlockKind.Paragraph, first.Kind);
        Assert.Equal("Preamble text that belongs to no heading at all.", TestHarness.Slice(document, first));
    }

    [Fact]
    public async Task HeadingLevelsAreRecordedAndTheSetextHeadingIsAHeading()
    {
        var document = await ExtractAsync("markdown/headings.md");

        var headings = document.Blocks
            .Where(b => b.Kind == DocumentBlockKind.Heading)
            .Select(b => (b.HeadingLevel, Text: TestHarness.Slice(document, b)))
            .ToList();

        Assert.Equal(5, headings.Count);
        Assert.Equal((1, "# Top level"), headings[0]);
        Assert.Equal((2, "## Second level"), headings[1]);
        Assert.Equal((3, "### Third level"), headings[2]);
        Assert.Equal((1, "Setext heading\n=============="), headings[3]);
        Assert.Equal((4, "#### Fourth level"), headings[4]);
    }

    [Fact]
    public async Task AHashLineInsideAFenceIsCodeAndNotAHeading()
    {
        var document = await ExtractAsync("markdown/headings.md");

        var code = Assert.Single(document.Blocks, b => b.Kind == DocumentBlockKind.Code);
        var slice = TestHarness.Slice(document, code);

        Assert.Contains("# not a heading, this line is inside a fence", slice, StringComparison.Ordinal);
        Assert.DoesNotContain(
            document.Blocks.Where(b => b.Kind == DocumentBlockKind.Heading),
            b => TestHarness.Slice(document, b).Contains("inside a fence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TildeFencesNestedBackticksAndIndentedCodeAreEachOneCodeBlock()
    {
        var document = await ExtractAsync("markdown/fences.md");

        var code = document.Blocks.Where(b => b.Kind == DocumentBlockKind.Code).ToList();
        Assert.Equal(3, code.Count);

        var tilde = TestHarness.Slice(document, code[0]);
        Assert.StartsWith("~~~", tilde, StringComparison.Ordinal);
        Assert.Contains("## not a heading, this line is inside a tilde fence", tilde, StringComparison.Ordinal);

        // A four-backtick fence containing three backticks is ONE block, not three.
        var fourTick = TestHarness.Slice(document, code[1]);
        Assert.StartsWith("````", fourTick, StringComparison.Ordinal);
        Assert.Contains("three backticks inside a four-tick fence", fourTick, StringComparison.Ordinal);

        Assert.Contains("## not a heading either", TestHarness.Slice(document, code[2]), StringComparison.Ordinal);

        var heading = Assert.Single(document.Blocks, b => b.Kind == DocumentBlockKind.Heading);
        Assert.Equal("# Fences", TestHarness.Slice(document, heading));
    }

    [Fact]
    public async Task TablesBecomeRowsAndListsBecomeTopLevelItems()
    {
        var document = await ExtractAsync("markdown/tables-lists.md");

        var rows = document.Blocks.Where(b => b.Kind == DocumentBlockKind.TableRow).ToList();
        Assert.Equal(4, rows.Count);
        // Markdig's TableRow span covers the row's CONTENT, pipes excluded - measured, not assumed.
        Assert.Equal("Column A | Column B", TestHarness.Slice(document, rows[0]).Trim());
        Assert.Equal("a3 | b3", TestHarness.Slice(document, rows[3]).Trim());

        var items = document.Blocks.Where(b => b.Kind == DocumentBlockKind.ListItem).ToList();
        Assert.Equal(4, items.Count);
        Assert.Contains("first bullet", TestHarness.Slice(document, items[0]), StringComparison.Ordinal);
        Assert.Contains("nested bullet", TestHarness.Slice(document, items[0]), StringComparison.Ordinal);
        Assert.Contains("second bullet", TestHarness.Slice(document, items[1]), StringComparison.Ordinal);
        Assert.Contains("first ordered item", TestHarness.Slice(document, items[2]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawHtmlIsOneBlockAndNotProseToBeSplit()
    {
        var document = await ExtractAsync("markdown/raw-html.md");

        Assert.Equal(3, document.Blocks.Count);
        Assert.Equal(DocumentBlockKind.Heading, document.Blocks[0].Kind);

        var html = TestHarness.Slice(document, document.Blocks[1]);
        Assert.StartsWith("<div class=\"note\">", html, StringComparison.Ordinal);
        Assert.EndsWith("</div>", html.TrimEnd('\n'), StringComparison.Ordinal);

        Assert.Contains("<em>inline</em>", TestHarness.Slice(document, document.Blocks[2]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMarkdownDocumentCarriesTheMarkdownExtractorIdentity()
    {
        var document = await ExtractAsync("markdown/tables-lists.md");

        Assert.Equal("markdown", document.ExtractorId);
        Assert.Equal(1, document.ExtractorVersion);
        Assert.Equal(IngestionMediaTypes.Markdown, document.MediaType);
        Assert.True(document.HasTextLayer);
        Assert.Null(document.PageCount);
    }

    private static async Task<ExtractedDocument> ExtractAsync(string fixture) =>
        await new MarkdownExtractor()
            .ExtractAsync(
                FixtureCorpus.Item(fixture),
                TestHarness.Context(new CapturingLogger()),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
}
