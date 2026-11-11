using System.Text;
using Qavren.Edge.Ingestion.Tests.Extraction;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// What a Markdown heading is filed under. The chunker trimmed every <c>#</c> from both ends of the
/// heading line, so a heading that ENDS in one - "C#", "F#", an issue number - lost it, and every
/// chunk under it carried the wrong breadcrumb into the index.
/// </summary>
public class HeadingTitleTests
{
    [Theory]
    [InlineData("# Intro to C#", "Intro to C#")]
    [InlineData("## F# and C# ##", "F# and C#")]
    [InlineData("# foo#", "foo#")]
    [InlineData("### Issue #42", "Issue #42")]
    [InlineData("## Title ##", "Title")]
    [InlineData("## Title ###   ", "Title")]
    [InlineData("#\tTabbed #", "Tabbed")]
    [InlineData("# Title", "Title")]
    [InlineData("#", "")]
    [InlineData("### ###", "")]
    [InlineData("Setext title", "Setext title")]
    [InlineData("#hashtag", "#hashtag")]
    [InlineData("####### seven is not a heading", "####### seven is not a heading")]
    public void TheHeadingTextIsWhatCommonMarkSaysItIs(string line, string expected) =>
        Assert.Equal(expected, MarkdownHeadingChunker.AtxHeadingContent(line));

    [Fact]
    public async Task AHeadingEndingInAHashIsFiledUnderItsWholeTitle()
    {
        var markdown = "# Intro to C#\n\nThe body of the first section.\n\n## Pattern matching in C# ##\n\nThe body of the second.\n";
        var item = new DocumentSourceItem(
            "csharp.md",
            IngestionMediaTypes.Markdown,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(markdown))));

        var document = await new MarkdownExtractor()
            .ExtractAsync(item, TestHarness.Context(new CapturingLogger()), TestContext.Current.CancellationToken);

        var options = new ResolvedChunkOptions(
            MaxTokens: 200,
            OverlapTokens: 0,
            MinTokens: 1,
            HeadingPathTokenBudget: 32,
            SpecialTokenOverhead: 2,
            DocumentPrefixTokens: 0,
            SplitHeadingLevels: [1, 2, 3],
            PrependHeadingPath: true,
            IncludePreamble: true,
            MergeShortSections: false,
            SentenceAware: true,
            RepeatTableHeaderRow: true,
            Overflow: ChunkOverflow.Split);

        var chunks = new MarkdownHeadingChunker().Chunk(document, options, new FakeChunkTokenizer()).ToList();

        Assert.Equal(["Intro to C#"], chunks[0].HeadingPath);
        Assert.Equal(["Intro to C#", "Pattern matching in C#"], chunks[1].HeadingPath);
    }

    [Fact]
    public void TheChunkerVersionMovedSoStoredChunksAreReChunked() =>
        Assert.Equal(2, new MarkdownHeadingChunker().Version);
}
