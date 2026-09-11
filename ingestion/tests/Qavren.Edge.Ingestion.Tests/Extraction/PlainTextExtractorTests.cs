using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// Spec 7.2 over the committed text corpus. Every assertion about a block is an assertion about a
/// VERBATIM slice of <see cref="ExtractedDocument.Text"/> - that is the offset contract, tested.
/// </summary>
public class PlainTextExtractorTests
{
    private static readonly string[] ExpectedExtensions = [".txt", ".log", ".csv", ".text"];
    private static readonly string[] ExpectedMediaTypes = [IngestionMediaTypes.PlainText];

    [Fact]
    public async Task ThreeParagraphsBecomeThreeParagraphBlocks()
    {
        var document = await ExtractAsync("text/three-paragraphs.txt");

        Assert.Equal("text", document.ExtractorId);
        Assert.Equal(1, document.ExtractorVersion);
        Assert.All(document.Blocks, b => Assert.Equal(DocumentBlockKind.Paragraph, b.Kind));

        Assert.Collection(
            document.Blocks,
            b => Assert.Equal("The first paragraph is short.", TestHarness.Slice(document, b)),
            b => Assert.Equal(
                "The second paragraph has two sentences. It exists so a sentence-aware cut has somewhere to land.",
                TestHarness.Slice(document, b)),
            b => Assert.Equal(
                "The third paragraph ends the file without a trailing newline.",
                TestHarness.Slice(document, b)));
    }

    [Fact]
    public async Task BlockOffsetsAreAscendingAndInsideTheBuffer()
    {
        var document = await ExtractAsync("text/three-paragraphs.txt");

        var previousEnd = 0;
        foreach (var block in document.Blocks)
        {
            Assert.True(block.Start >= previousEnd);
            Assert.True(block.Start < block.End);
            Assert.True(block.End <= document.Text.Length);
            previousEnd = block.End;
        }
    }

    [Fact]
    public async Task EmptyYieldsNoBlocksAndDoesNotThrow()
    {
        var document = await ExtractAsync("text/empty.txt");

        Assert.Equal(string.Empty, document.Text);
        Assert.Empty(document.Blocks);
    }

    [Fact]
    public async Task WhitespaceOnlyYieldsNoBlocksAndDoesNotThrow()
    {
        var document = await ExtractAsync("text/whitespace-only.txt");

        Assert.Equal("   \n\n\t\n", document.Text);
        Assert.Empty(document.Blocks);
    }

    [Fact]
    public async Task CrlfAndLoneCrAreNormalisedBeforeOffsetsAreTaken()
    {
        var document = await ExtractAsync("text/crlf-and-lone-cr.txt");

        Assert.False(document.Text.Contains('\r'));
        Assert.Collection(
            document.Blocks,
            b => Assert.Equal("line one\nline two\nline three", TestHarness.Slice(document, b)),
            b => Assert.Equal("second block after a CRLF blank line", TestHarness.Slice(document, b)));
    }

    [Fact]
    public async Task NormalisationOffLeavesTheRawDecodeAndItsOffsets()
    {
        var document = await ExtractAsync(
            "text/crlf-and-lone-cr.txt", new ExtractionOptions { NormalizeText = false });

        Assert.True(document.Text.Contains('\r'));
        Assert.Equal("line one\r\nline two\rline three", TestHarness.Slice(document, document.Blocks[0]));
    }

    [Fact]
    public async Task TheByteOrderMarkNeverReachesTheFirstBlock()
    {
        var document = await ExtractAsync("text/bom.txt");

        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal(
            "A document that opens with a UTF-8 byte order mark.",
            TestHarness.Slice(document, document.Blocks[0]));
        Assert.Equal("Second paragraph.", TestHarness.Slice(document, document.Blocks[1]));
    }

    [Fact]
    public async Task AFiveThousandCharacterRunIsOneBlockCoveringTheWholeFile()
    {
        var document = await ExtractAsync("text/long-token.txt");

        var block = Assert.Single(document.Blocks);
        Assert.Equal(0, block.Start);
        Assert.Equal(document.Text.Length, block.End);
        Assert.Equal(5000, document.Text.Length);
        Assert.False(document.Text.Contains(' '));
    }

    [Fact]
    public async Task UnicodeSurvivesTheRoundTripIntact()
    {
        var document = await ExtractAsync("text/unicode.txt");

        var block = Assert.Single(document.Blocks);
        var slice = TestHarness.Slice(document, block);

        Assert.StartsWith("NFC:", slice, StringComparison.Ordinal);
        Assert.EndsWith("TR: I \u0130 i \u0131", slice, StringComparison.Ordinal);
        Assert.Contains("\U0001F468\u200D\U0001F469\u200D\U0001F467", slice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExtractorDeclaresSpecSevenTwosIdentity()
    {
        var extractor = new PlainTextExtractor();

        Assert.Equal("text", extractor.Id);
        Assert.Equal(1, extractor.Version);
        Assert.Equal(ExpectedExtensions, extractor.Extensions);
        Assert.Equal(ExpectedMediaTypes, extractor.MediaTypes);
    }

    [Fact]
    public void StrictUtf8IsOffByDefault() => Assert.False(new PlainTextExtractorOptions().StrictUtf8);

    private static async Task<ExtractedDocument> ExtractAsync(string fixture, ExtractionOptions? options = null) =>
        await new PlainTextExtractor()
            .ExtractAsync(
                FixtureCorpus.Item(fixture),
                TestHarness.Context(new CapturingLogger(), options),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
}
