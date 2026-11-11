using System.IO.Compression;
using System.Text;
using Qavren.Edge.Ingestion.OpenXml;
using Xunit;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// Two ways the DOCX extractor indexed something other than what the document says: a tracked
/// property change read as the paragraph's current properties, and smart-tagged text dropped.
/// Both change extracted output for documents that already extracted, which is what
/// <see cref="DocxTextExtractor.Version"/> 2 records.
/// </summary>
public class DocxFidelityTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATrackedStyleChangeDoesNotOverwriteTheParagraphsCurrentStyle(bool streaming)
    {
        // Restyled from Normal to Heading 1 with track changes on: the current w:pStyle is
        // Heading1, and w:pPrChange carries the OLD w:pPr, whose w:pStyle is Normal.
        var body =
            "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/>" +
            "<w:pPrChange w:id=\"1\" w:author=\"a\"><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr></w:pPrChange>" +
            "</w:pPr><w:r><w:t>Now a heading</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>Body text.</w:t></w:r></w:p>";

        var document = await ExtractAsync(Package("tracked.docx", body), streaming);

        Assert.Equal(DocumentBlockKind.Heading, document.Blocks[0].Kind);
        Assert.Equal(1, document.Blocks[0].HeadingLevel);
        Assert.Equal("Now a heading", document.Text[document.Blocks[0].Start..document.Blocks[0].End]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATrackedOutlineLevelChangeDoesNotOverwriteTheCurrentLevelEither(bool streaming)
    {
        var body =
            "<w:p><w:pPr><w:outlineLvl w:val=\"1\"/>" +
            "<w:pPrChange w:id=\"1\" w:author=\"a\"><w:pPr><w:outlineLvl w:val=\"9\"/></w:pPr></w:pPrChange>" +
            "</w:pPr><w:r><w:t>Level two</w:t></w:r></w:p>";

        var document = await ExtractAsync(Package("tracked-level.docx", body), streaming);

        Assert.Equal(DocumentBlockKind.Heading, document.Blocks[0].Kind);
        Assert.Equal(2, document.Blocks[0].HeadingLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextInsideASmartTagIsKeptInItsPlaceAndTheParagraphsAroundItAreUntouched(bool streaming)
    {
        // Word 2003 and 2007 wrapped names, places and dates in w:smartTag. Current schemas dropped
        // the element, the SDK reads everything inside it as unknown, and the words in there -
        // usually the very names a search is for - were dropped from the index.
        var body =
            "<w:p><w:r><w:t xml:space=\"preserve\">Meet me in </w:t></w:r>" +
            "<w:smartTag w:uri=\"urn:schemas-microsoft-com:office:smarttags\" w:element=\"place\">" +
            "<w:smartTag w:uri=\"urn:schemas-microsoft-com:office:smarttags\" w:element=\"City\">" +
            "<w:r><w:t>Seattle</w:t></w:r><w:r><w:tab/><w:t>WA</w:t></w:r></w:smartTag></w:smartTag>" +
            "<w:r><w:t xml:space=\"preserve\"> tomorrow.</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Agenda</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>Second paragraph.</w:t></w:r></w:p>";

        var document = await ExtractAsync(Package("smart-tags.docx", body), streaming);

        Assert.Equal(3, document.Blocks.Count);
        Assert.Equal("Meet me in Seattle\tWA tomorrow.", Slice(document, 0));
        Assert.Equal(DocumentBlockKind.Paragraph, document.Blocks[0].Kind);
        Assert.Equal("Agenda", Slice(document, 1));
        Assert.Equal(DocumentBlockKind.Heading, document.Blocks[1].Kind);
        Assert.Equal("Second paragraph.", Slice(document, 2));
    }

    [Fact]
    public async Task TheTwoFeedsStillAgreeOnASmartTaggedDocument()
    {
        var body =
            "<w:p><w:smartTag w:uri=\"u\" w:element=\"e\"><w:r><w:t>A</w:t></w:r></w:smartTag>" +
            "<w:r><w:t>B</w:t></w:r><w:smartTag w:uri=\"u\" w:element=\"e\"><w:r><w:t>C</w:t></w:r></w:smartTag></w:p>" +
            "<w:p><w:smartTag w:uri=\"u\" w:element=\"e\"><w:r><w:t>D</w:t></w:r></w:smartTag></w:p>";

        var dom = await ExtractAsync(Package("dom.docx", body), streaming: false);
        var streamed = await ExtractAsync(Package("streamed.docx", body), streaming: true);

        Assert.Equal(2, dom.Blocks.Count);
        Assert.Equal("ABC", Slice(dom, 0));
        Assert.Equal("D", Slice(dom, 1));
        Assert.Equal(dom.Text, streamed.Text);
        Assert.Equal(
            dom.Blocks.Select(b => (b.Kind, b.Start, b.End)),
            streamed.Blocks.Select(b => (b.Kind, b.Start, b.End)));
    }

    /// <summary>
    /// What the streaming feed relies on, pinned so an SDK upgrade that moves it fails here and not
    /// as a silently mis-nested document: <c>LoadCurrentElement</c> leaves the cursor on the loaded
    /// element's OWN end tag.
    /// </summary>
    [Fact]
    public void LoadCurrentElementLeavesTheCursorOnTheLoadedElementsEndTag()
    {
        var bytes = PackageBytes("<w:p><w:smartTag w:uri=\"u\" w:element=\"e\"><w:r><w:t>A</w:t></w:r></w:smartTag></w:p>");
        using var stream = new MemoryStream(bytes);
        using var package = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, isEditable: false);
        using var reader = new DocumentFormat.OpenXml.OpenXmlPartReader(package.MainDocumentPart!);

        while (reader.Read() && !(reader.IsStartElement && reader.LocalName == "t"))
        {
        }

        Assert.Equal("A", reader.LoadCurrentElement()?.InnerText);
        Assert.True(reader.IsEndElement);
        Assert.Equal("t", reader.LocalName);
    }

    [Fact]
    public void TheExtractorVersionMovedSoStoredDocxChunksAreReExtracted() =>
        Assert.Equal(2, new DocxTextExtractor().Version);

    private static string Slice(ExtractedDocument document, int block) =>
        document.Text[document.Blocks[block].Start..document.Blocks[block].End];

    private static async Task<ExtractedDocument> ExtractAsync(DocumentSourceItem item, bool streaming)
    {
        var options = new DocxExtractorOptions { StreamingThresholdBytes = streaming ? 0 : long.MaxValue };
        return await new DocxTextExtractor(options)
            .ExtractAsync(item, TestHarness.Context(new CapturingLogger()), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static DocumentSourceItem Package(string name, string bodyXml) =>
        FixtureCorpus.Item(name, PackageBytes(bodyXml));

    /// <summary>The committed <c>headings</c> tree, with its body replaced.</summary>
    private static byte[] PackageBytes(string bodyXml)
    {
        var document = $"<w:document xmlns:w=\"{W}\"><w:body>{bodyXml}</w:body></w:document>";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (partName, content) in DeterministicOpc.Parts("headings"))
            {
                using var stream = zip.CreateEntry(partName, CompressionLevel.Fastest).Open();
                stream.Write(string.Equals(partName, "word/document.xml", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetBytes(document)
                    : content);
            }
        }

        return buffer.ToArray();
    }
}
