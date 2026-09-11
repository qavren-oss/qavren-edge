using Qavren.Edge.Ingestion.Internal;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// Spec 7.1's registry: media type before extension, consumer registrations before the built-ins,
/// 6004 at registration for a duplicate id, and a 6101 whose remediation names the right satellite.
/// </summary>
public class ExtractorRegistryTests
{
    private static DocumentSourceItem Item(string documentId, string? mediaType = null) =>
        new(
            documentId,
            mediaType ?? IngestionMediaTypes.FromExtension(documentId),
            _ => ValueTask.FromResult(Stream.Null),
            Path: documentId);

    [Fact]
    public void TheBuiltInsAreTextThenMarkdown()
    {
        var registry = new DocumentExtractorRegistry();

        Assert.Collection(
            registry.Extractors,
            e => Assert.Equal("text", e.Id),
            e => Assert.Equal("markdown", e.Id));
        Assert.Equal("text:1, markdown:1", registry.Describe());
    }

    [Fact]
    public void DescribeListsEveryRegistrationInOrder()
    {
        var registry = new DocumentExtractorRegistry(
            [new StubExtractor("pdf", 3, [".pdf"], [IngestionMediaTypes.Pdf])]);

        Assert.Equal("pdf:3, text:1, markdown:1", registry.Describe());
    }

    [Fact]
    public void AConsumerExtractorWinsOverTheBuiltInForTheSameExtension()
    {
        var mine = new StubExtractor("mine", 1, [".md"], [IngestionMediaTypes.Markdown]);
        var registry = new DocumentExtractorRegistry([mine]);

        Assert.True(registry.TryResolve(Item("notes.md"), out var resolved));
        Assert.Same(mine, resolved);
    }

    [Fact]
    public void MediaTypeIsTriedBeforeExtension()
    {
        // The extension says markdown; the declared media type says plain text. Media type wins.
        var item = Item("misnamed.md", IngestionMediaTypes.PlainText);
        var registry = new DocumentExtractorRegistry();

        Assert.True(registry.TryResolve(item, out var resolved));
        Assert.Equal("text", resolved.Id);
    }

    [Fact]
    public void ExtensionResolvesWhenTheMediaTypeIsUnknown()
    {
        var item = Item("notes.markdown", IngestionMediaTypes.Unknown);
        var registry = new DocumentExtractorRegistry();

        Assert.True(registry.TryResolve(item, out var resolved));
        Assert.Equal("markdown", resolved.Id);
    }

    [Fact]
    public void TwoExtractorsSharingAnIdThrowSixZeroZeroFourAtRegistration()
    {
        var thrown = Assert.Throws<EdgeConfigurationException>(
            () => new DocumentExtractorRegistry([new StubExtractor("text", 9, [".foo"], [])]));

        Assert.Equal(EdgeErrorCode.IngestionDuplicateExtractorId, thrown.Code);
        Assert.Contains("'text'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvedPdfNamesAddPdfExtractor()
    {
        var registry = new DocumentExtractorRegistry();
        var item = Item("report.pdf");

        Assert.False(registry.TryResolve(item, out _));

        var failure = registry.NotFound(item);
        Assert.Equal(EdgeErrorCode.ExtractorNotFound, failure.Code);
        Assert.Contains("AddPdfExtractor()", failure.Remediation!, StringComparison.Ordinal);
        Assert.Contains("Qavren.Edge.Ingestion.Pdf", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvedDocxNamesAddDocxExtractor()
    {
        var registry = new DocumentExtractorRegistry();
        var item = Item("contract.docx");

        Assert.False(registry.TryResolve(item, out _));

        var failure = registry.NotFound(item);
        Assert.Equal(EdgeErrorCode.ExtractorNotFound, failure.Code);
        Assert.Contains("AddDocxExtractor()", failure.Remediation!, StringComparison.Ordinal);
        Assert.Contains("Qavren.Edge.Ingestion.OpenXml", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvedUnknownFormatFallsBackToAddDocumentExtractor()
    {
        var registry = new DocumentExtractorRegistry();
        var item = Item("photo.heic");

        Assert.False(registry.TryResolve(item, out _));

        var failure = registry.NotFound(item);
        Assert.Contains("AddDocumentExtractor()", failure.Remediation!, StringComparison.Ordinal);
        Assert.Contains("text:1, markdown:1", failure.Remediation!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notes.md", IngestionMediaTypes.Markdown)]
    [InlineData("notes.MARKDOWN", IngestionMediaTypes.Markdown)]
    [InlineData("a/b/c.txt", IngestionMediaTypes.PlainText)]
    [InlineData(".log", IngestionMediaTypes.PlainText)]
    [InlineData("csv", IngestionMediaTypes.PlainText)]
    [InlineData("report.pdf", IngestionMediaTypes.Pdf)]
    [InlineData("deck.pptx", IngestionMediaTypes.Unknown)]
    [InlineData("README", IngestionMediaTypes.Unknown)]
    public void MediaTypeMappingMatchesTheLastDotSegment(string input, string expected) =>
        Assert.Equal(expected, IngestionMediaTypes.FromExtension(input));
}
