using System.Text;
using Microsoft.Extensions.DataIngestion;
using Xunit;

namespace Qavren.Edge.Ingestion.DataIngestion.Tests;

/// <summary>
/// <see cref="EdgeChunkerMediAdapter"/> carries the caller's budget and every draft field;
/// <see cref="MediReaderAdapter"/> makes a MEDI reader an SP3 extractor that opens through
/// <see cref="DocumentSourceItem.OpenAsync"/>.
/// </summary>
public sealed class AdapterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>True only when real NFC is available; false under InvariantGlobalization.</summary>
    private static bool ComposesNfc => "e\u0301".Normalize(NormalizationForm.FormC).Length == 1;

    [Fact]
    public async Task The_chunker_adapter_emits_SP3s_draft_fields_as_MEDI_chunk_metadata()
    {
        var tokenizer = new WhitespaceTokenizer();
        var options = new ChunkOptions { MergeShortSections = false, MinTokens = 1 }
            .Resolve(ChunkModelProfile.MiniLmL6V2Int8, tokenizer);
        var adapter = new EdgeChunkerMediAdapter(new MarkdownHeadingChunker(), options, tokenizer);
        var document = EdgeDocumentConverter.ToMedi(await ExtractAsync("# Alpha\n\nOne two three.\n\n## Beta\n\nFour five.\n"));

        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in adapter.ProcessAsync(document, Token))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Same(document, chunks[0].Document);

        Assert.Contains("One two three.", chunks[0].Content, StringComparison.Ordinal);
        Assert.Equal("Alpha", chunks[0].Context);
        Assert.Equal("Alpha\n\n" + chunks[0].Content, chunks[0].Metadata[EdgeChunkerMediAdapter.EmbedTextKey]);
        Assert.Equal(0, chunks[0].Metadata[EdgeChunkerMediAdapter.OrdinalKey]);
        Assert.True(Assert.IsType<int>(chunks[0].Metadata[EdgeChunkerMediAdapter.TokenCountKey]) >= 3);
        Assert.Equal("Paragraph", chunks[0].Metadata[EdgeChunkerMediAdapter.BlockKindKey]);
        Assert.Equal(-1, chunks[0].Metadata[EdgeChunkerMediAdapter.PageKey]);

        Assert.Contains("Four five.", chunks[1].Content, StringComparison.Ordinal);
        Assert.Equal("Alpha › Beta", chunks[1].Context);
        Assert.Equal(1, chunks[1].Metadata[EdgeChunkerMediAdapter.OrdinalKey]);

        // The offsets index the text FromMedi rebuilt, and the slice is the content.
        var rebuilt = EdgeDocumentConverter.FromMedi(document).Text;
        var start = Assert.IsType<int>(chunks[1].Metadata[EdgeChunkerMediAdapter.CharStartKey]);
        var end = Assert.IsType<int>(chunks[1].Metadata[EdgeChunkerMediAdapter.CharEndKey]);
        Assert.Equal(chunks[1].Content, rebuilt[start..end]);
    }

    [Fact]
    public async Task The_chunker_adapter_honours_the_budget_it_was_handed_not_MEDIs_defaults()
    {
        var tokenizer = new WhitespaceTokenizer();
        var words = string.Join(' ', Enumerable.Range(1, 60).Select(i => "w" + i)) + ".";
        var document = EdgeDocumentConverter.ToMedi(await ExtractAsync(words + "\n"));

        var wide = new ChunkOptions().Resolve(ChunkModelProfile.MiniLmL6V2Int8, tokenizer);
        var narrow = new ChunkOptions { MaxTokens = 20, OverlapTokens = 0, MinTokens = 1 }
            .Resolve(ChunkModelProfile.MiniLmL6V2Int8, tokenizer);

        Assert.Equal(1, await CountAsync(new EdgeChunkerMediAdapter(new PlainChunker(), wide, tokenizer), document));
        Assert.True(await CountAsync(new EdgeChunkerMediAdapter(new PlainChunker(), narrow, tokenizer), document) >= 3);
    }

    [Fact]
    public async Task The_reader_adapter_is_an_extractor_that_opens_through_the_item()
    {
        var medi = new IngestionDocument("ignored-by-adapter");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentHeader("# Page\r\n") { Level = 1, Text = "\uFEFFPage\r\n" });
        section.Elements.Add(new IngestionDocumentParagraph("Cafe\u0301 body") { Text = "Cafe\u0301 body" });
        medi.Sections.Add(section);
        var reader = new StubReader(medi);

        var adapter = new MediReaderAdapter(reader, "html", [".html"], ["text/html"], version: 3);
        var opens = 0;
        var bytes = Encoding.UTF8.GetBytes("<html>irrelevant</html>");
        var item = new DocumentSourceItem(
            "docs/page.html",
            "text/html",
            _ =>
            {
                opens++;
                return new ValueTask<Stream>(new MemoryStream(bytes, writable: false));
            });
        var context = new ExtractionContext("src", "chunks", 1024, new ExtractionOptions(), new CapturingLogger());

        var extracted = await adapter.ExtractAsync(item, context, Token);

        Assert.Equal(1, opens);
        var read = Assert.Single(reader.Reads);
        Assert.Equal(("docs/page.html", "text/html", (long)bytes.Length), read);

        Assert.Equal("docs/page.html", extracted.DocumentId);
        Assert.Equal("html", extracted.ExtractorId);
        Assert.Equal(3, extracted.ExtractorVersion);
        Assert.Equal("text/html", extracted.MediaType);

        // Normalised BEFORE the buffer was built: no BOM, no CR, NFC - and the offsets still slice.
        // NFC is a no-op under the repo-wide InvariantGlobalization (see the core's TextNormalizer
        // remarks), so the composition half is asserted only where the runtime can compose.
        var cafe = ComposesNfc ? "Caf\u00E9 body" : "Cafe\u0301 body";
        Assert.Equal("Page\n\n\n" + cafe, extracted.Text);
        Assert.Equal(2, extracted.Blocks.Count);
        Assert.Equal("Page\n", extracted.Text[extracted.Blocks[0].Start..extracted.Blocks[0].End]);
        Assert.Equal(cafe, extracted.Text[extracted.Blocks[1].Start..extracted.Blocks[1].End]);
        Assert.Equal(DocumentBlockKind.Heading, extracted.Blocks[0].Kind);
        Assert.Equal(1, extracted.Blocks[0].HeadingLevel);
    }

    [Fact]
    public async Task The_reader_adapter_leaves_text_alone_when_normalisation_is_off()
    {
        var medi = new IngestionDocument("x");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("a\r\nb") { Text = "a\r\nb" });
        medi.Sections.Add(section);

        var adapter = new MediReaderAdapter(new StubReader(medi), "stub", [".x"], ["text/x"]);
        var item = new DocumentSourceItem("x", "text/x", _ => new ValueTask<Stream>(new MemoryStream()));
        var options = new ExtractionOptions { NormalizeText = false };
        var context = new ExtractionContext("src", "chunks", 1024, options, new CapturingLogger());

        var extracted = await adapter.ExtractAsync(item, context, Token);

        Assert.Equal("a\r\nb", extracted.Text);
    }

    private static async Task<ExtractedDocument> ExtractAsync(string markdown)
    {
        var bytes = Encoding.UTF8.GetBytes(markdown);
        var item = new DocumentSourceItem(
            "doc.md", IngestionMediaTypes.Markdown, _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)));
        var context = new ExtractionContext("src", "chunks", 1024 * 1024, new ExtractionOptions(), new CapturingLogger());
        return await new MarkdownExtractor().ExtractAsync(item, context, Token).ConfigureAwait(false);
    }

    private static async Task<int> CountAsync(EdgeChunkerMediAdapter adapter, IngestionDocument document)
    {
        var count = 0;
        await foreach (var _ in adapter.ProcessAsync(document, Token).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }
}
