using DocumentFormat.OpenXml.Packaging;
using Qavren.Edge.Ingestion.OpenXml;
using Xunit;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// The DOM path and the <c>OpenXmlPartReader</c> path over the SAME tree must produce identical
/// <c>ExtractedDocument.Text</c> and identical block spans — the only thing that keeps the two
/// traversals honest (spec 7.5). Plus the <c>DeterministicOpc</c> determinism test (spec 14.2).
/// </summary>
public class DocxStreamingEquivalenceTests
{
    private static readonly string[] TreeNames =
    [
        "headings", "run-split", "table", "numbered-list", "footnotes", "header-footer",
        "hyperlink", "textbox", "empty-body", "unknown-style", "localised-style",
    ];

    public static TheoryData<string> Trees => new(TreeNames);

    [Theory]
    [MemberData(nameof(Trees))]
    public async Task TheStreamingPathMatchesTheDomPathExactly(string tree)
    {
        var (dom, domLog) = await DocxExtractorTests.ExtractAsync(tree, o => o.StreamingThresholdBytes = long.MaxValue);
        var (sax, saxLog) = await DocxExtractorTests.ExtractAsync(tree, o => o.StreamingThresholdBytes = 0);

        Assert.Equal(dom.Text, sax.Text);
        Assert.Equal(dom.Blocks, sax.Blocks);
        Assert.Equal(TestHarness.Slices(dom), TestHarness.Slices(sax));

        // Event 913 records which mode ran, so the choice is observable.
        Assert.Contains("dom mode", domLog.MessageOf(EdgeIngestionEventIds.ExtractionStreamingMode), StringComparison.Ordinal);
        Assert.Contains("streaming mode", saxLog.MessageOf(EdgeIngestionEventIds.ExtractionStreamingMode), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public async Task TheStreamingPathMatchesTheDomPathWithEveryOptionFlipped(string tree)
    {
        static void Flip(DocxExtractorOptions o)
        {
            o.ExcludeHeadersAndFooters = false;
            o.IncludeTextBoxes = false;
            o.IncludeNotes = false;
            o.IncludeTables = false;
        }

        var (dom, _) = await DocxExtractorTests.ExtractAsync(tree, o =>
        {
            Flip(o);
            o.StreamingThresholdBytes = long.MaxValue;
        });
        var (sax, _) = await DocxExtractorTests.ExtractAsync(tree, o =>
        {
            Flip(o);
            o.StreamingThresholdBytes = 0;
        });

        Assert.Equal(dom.Text, sax.Text);
        Assert.Equal(dom.Blocks, sax.Blocks);
    }

    [Fact]
    public async Task TheDefaultThresholdSelectsTheDomForASmallDocument()
    {
        var (_, logger) = await DocxExtractorTests.ExtractAsync("run-split");

        Assert.Contains("dom mode", logger.MessageOf(EdgeIngestionEventIds.ExtractionStreamingMode), StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicOpcBuildsTheSameBytesTwice()
    {
        foreach (var tree in TreeNames)
        {
            Assert.Equal(DeterministicOpc.Build(tree), DeterministicOpc.Build(tree));
        }
    }

    [Fact]
    public void DeterministicOpcOrdersContentTypesThenRelsThenDocumentThenTheRest()
    {
        var names = DeterministicOpc.Parts("footnotes").Select(p => p.Name).ToArray();

        Assert.Equal(
            ["[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/_rels/document.xml.rels", "word/footnotes.xml"],
            names);
    }

    [Fact]
    public void DeterministicOpcPackagesOpenInTheOpenXmlSdk()
    {
        foreach (var tree in TreeNames)
        {
            using var stream = new MemoryStream(DeterministicOpc.Build(tree), writable: false);
            using var package = WordprocessingDocument.Open(stream, isEditable: false);

            Assert.NotNull(package.MainDocumentPart);
            Assert.NotNull(package.MainDocumentPart!.Document);
        }
    }

    [Fact]
    public void ElevenTreesAreCommitted()
    {
        var trees = FixtureCorpus.Under("docx/")
            .Select(k => k["docx/".Length..])
            .Select(k => k[..k.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(TreeNames.Order(StringComparer.Ordinal).ToArray(), trees);
    }
}
