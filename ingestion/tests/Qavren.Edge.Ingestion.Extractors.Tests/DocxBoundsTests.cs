using System.IO.Compression;
using System.Text;
using Qavren.Edge.Ingestion.OpenXml;
using Xunit;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// The two bounds a hostile DOCX is refused on: how deep its XML nests (256 elements) and how far
/// one XML part inflates (256 MiB). Both are 6104 on the DOCUMENT, raised before the Open XML SDK
/// builds anything from the part - its DOM load recurses once per level of nesting, and a stack
/// overflow is not an exception a run survives.
/// </summary>
/// <remarks>
/// The packages are assembled here rather than committed as trees: <c>ElevenTreesAreCommitted</c>
/// pins the corpus at eleven, and a quarter-gigabyte part is not something to keep in git. Every
/// other part comes from the committed <c>headings</c> tree.
/// </remarks>
public class DocxBoundsTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABodyNestedPastTheDepthCeilingIsSixOneZeroFourInEitherMode(bool streaming)
    {
        // About 400 levels. Far short of what overflows a stack - a test cannot survive the thing
        // it guards against - but past the ceiling that stops one.
        var item = Package("deep.docx", "word/document.xml", NestedBody(contentControls: 200));

        var thrown = await Assert.ThrowsAsync<EdgeExtractionException>(() => ExtractAsync(item, streaming));

        Assert.Equal(EdgeErrorCode.DocumentMalformed, thrown.Code);
        Assert.Equal("docx", thrown.ExtractorName);
        Assert.Contains("deep.docx", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("256", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("word/document.xml", thrown.Message, StringComparison.Ordinal);
        Assert.NotNull(thrown.Remediation);
    }

    [Fact]
    public async Task AStylesPartNestedPastTheDepthCeilingIsSixOneZeroFourToo()
    {
        // The styles part is always read as a DOM, whatever the size of the document.
        var styles = new StringBuilder($"<w:styles xmlns:w=\"{W}\">");
        Nest(styles, "w:style", 400, string.Empty);
        styles.Append("</w:styles>");

        var item = Package("deep-styles.docx", "word/styles.xml", styles.ToString());

        var thrown = await Assert.ThrowsAsync<EdgeExtractionException>(() => ExtractAsync(item, streaming: false));

        Assert.Equal(EdgeErrorCode.DocumentMalformed, thrown.Code);
        Assert.Contains("word/styles.xml", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestingWellInsideTheCeilingExtractsAsItAlwaysDid(bool streaming)
    {
        // About two hundred levels is several times anything a word processor writes - ten tables
        // nested in each other are about fifty - and it is still a document.
        var item = Package("nested.docx", "word/document.xml", NestedBody(contentControls: 97));

        var document = await ExtractAsync(item, streaming);

        Assert.Equal("Text at the bottom of the nest.", document.Text);
        Assert.Single(document.Blocks);
    }

    [Fact]
    public async Task ThePartAtTheCeilingIsADocumentAndOneLevelPastItIsNot()
    {
        // document(1) body(2), two per content control, then p r t: the text sits at 2k + 5.
        // k = 125 puts it at 255 and k = 126 at 257 - so the ceiling is exactly where it says.
        var at = await ExtractAsync(Package("at.docx", "word/document.xml", NestedBody(contentControls: 125)), streaming: false);
        Assert.Equal("Text at the bottom of the nest.", at.Text);

        var past = await Assert.ThrowsAsync<EdgeExtractionException>(
            () => ExtractAsync(Package("past.docx", "word/document.xml", NestedBody(contentControls: 126)), streaming: false));
        Assert.Equal(EdgeErrorCode.DocumentMalformed, past.Code);
    }

    [Fact]
    public async Task APartThatInflatesPastTheSizeCeilingIsSixOneZeroFourBeforeItIsRead()
    {
        // A third of a megabyte on disk, 257 MiB once inflated: whitespace between two elements,
        // which is as cheap as a zip bomb gets and parses as a perfectly valid document.
        const long Padding = 257L * 1024 * 1024;
        var item = Package(
            "bomb.docx",
            "word/document.xml",
            stream =>
            {
                Write(stream, $"<w:document xmlns:w=\"{W}\"><w:body>");
                var spaces = new byte[64 * 1024];
                Array.Fill(spaces, (byte)' ');
                for (var written = 0L; written < Padding; written += spaces.Length)
                {
                    stream.Write(spaces);
                }

                Write(stream, "<w:p><w:r><w:t>after the padding</w:t></w:r></w:p></w:body></w:document>");
            });

        var thrown = await Assert.ThrowsAsync<EdgeExtractionException>(() => ExtractAsync(item, streaming: false));

        Assert.Equal(EdgeErrorCode.DocumentMalformed, thrown.Code);
        Assert.Contains("bomb.docx", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("word/document.xml", thrown.Message, StringComparison.Ordinal);
        Assert.NotNull(thrown.Remediation);
    }

    private static async Task<ExtractedDocument> ExtractAsync(DocumentSourceItem item, bool streaming)
    {
        var options = new DocxExtractorOptions { StreamingThresholdBytes = streaming ? 0 : long.MaxValue };
        return await new DocxTextExtractor(options)
            .ExtractAsync(item, TestHarness.Context(new CapturingLogger()), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One paragraph inside <paramref name="contentControls"/> block-level content controls nested
    /// in one another - the shape a form template built from templates actually takes. Each adds
    /// two levels (<c>w:sdt</c>, <c>w:sdtContent</c>).
    /// </summary>
    private static string NestedBody(int contentControls)
    {
        var body = new StringBuilder($"<w:document xmlns:w=\"{W}\"><w:body>");
        for (var i = 0; i < contentControls; i++)
        {
            body.Append("<w:sdt><w:sdtContent>");
        }

        body.Append("<w:p><w:r><w:t>Text at the bottom of the nest.</w:t></w:r></w:p>");
        for (var i = 0; i < contentControls; i++)
        {
            body.Append("</w:sdtContent></w:sdt>");
        }

        body.Append("</w:body></w:document>");
        return body.ToString();
    }

    private static void Nest(StringBuilder builder, string element, int levels, string innermost)
    {
        for (var i = 0; i < levels; i++)
        {
            builder.Append('<').Append(element).Append('>');
        }

        builder.Append(innermost);
        for (var i = 0; i < levels; i++)
        {
            builder.Append("</").Append(element).Append('>');
        }
    }

    private static DocumentSourceItem Package(string name, string replacedPart, string xml) =>
        Package(name, replacedPart, stream => Write(stream, xml));

    /// <summary>The <c>headings</c> tree with one part written by the test, streamed so it can be large.</summary>
    private static DocumentSourceItem Package(string name, string replacedPart, Action<Stream> write)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (partName, content) in DeterministicOpc.Parts("headings"))
            {
                using var stream = zip.CreateEntry(partName, CompressionLevel.Fastest).Open();
                if (string.Equals(partName, replacedPart, StringComparison.Ordinal))
                {
                    write(stream);
                }
                else
                {
                    stream.Write(content);
                }
            }
        }

        return FixtureCorpus.Item(name, buffer.ToArray());
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.UTF8.GetBytes(text));
}
