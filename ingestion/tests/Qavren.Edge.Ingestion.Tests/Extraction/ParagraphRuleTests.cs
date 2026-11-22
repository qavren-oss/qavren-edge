using System.Text;
using Microsoft.Extensions.Logging.Testing;
using Qavren.Edge.Ingestion.Internal;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// The blank-line paragraph rule, pinned to exact offsets. Block boundaries become chunk boundaries,
/// content hashes and vector reuse, so an off-by-one here re-embeds a corpus; the fixture-driven
/// tests assert slices, and these assert the numbers.
/// </summary>
public class ParagraphRuleTests
{
    [Theory]
    [InlineData("  alpha\nbeta  \n\n \t \n\ngamma\n", new[] { 2, 12, 21, 26 })]
    [InlineData("\n\nsolo", new[] { 2, 6 })]
    [InlineData("one\ntwo", new[] { 0, 7 })]
    [InlineData("a\n\n\nb\n\n", new[] { 0, 1, 4, 5 })]
    [InlineData("x", new[] { 0, 1 })]
    [InlineData("\n", new int[0])]
    [InlineData(" \t ", new int[0])]
    public async Task PlainTextSplitsOnBlankLinesAndTrimsEachRunToItsNonBlankCharacters(string text, int[] offsets)
    {
        var item = new DocumentSourceItem(
            "doc",
            IngestionMediaTypes.PlainText,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text))));

        var document = await new PlainTextExtractor()
            .ExtractAsync(item, TestHarness.Context(new FakeLogger()), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var expected = new List<DocumentBlock>();
        for (var i = 0; i < offsets.Length; i += 2)
        {
            expected.Add(new DocumentBlock(DocumentBlockKind.Paragraph, offsets[i], offsets[i + 1], HeadingLevel: null, PageNumber: null));
        }

        Assert.Equal(text, document.Text);
        Assert.Equal(expected, document.Blocks);
    }

    [Fact]
    public void TheSharedSplitterShiftsEveryOffsetAndStampsThePageWithoutTouchingWhatIsAlreadyThere()
    {
        // The PDF satellite links this same file and calls it once per page, appending to one list.
        var existing = new DocumentBlock(DocumentBlockKind.Paragraph, 0, 5, HeadingLevel: null, PageNumber: 1);
        var blocks = new List<DocumentBlock> { existing };

        ParagraphSplitter.Split("  alpha\nbeta  \n\n \t \n\ngamma\n", baseOffset: 100, pageNumber: 3, blocks);

        DocumentBlock[] expected =
        [
            existing,
            new(DocumentBlockKind.Paragraph, 102, 112, HeadingLevel: null, PageNumber: 3),
            new(DocumentBlockKind.Paragraph, 121, 126, HeadingLevel: null, PageNumber: 3),
        ];
        Assert.Equal(expected, blocks);
    }
}
