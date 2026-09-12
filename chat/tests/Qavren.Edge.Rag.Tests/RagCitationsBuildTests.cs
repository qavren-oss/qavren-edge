using Microsoft.Extensions.AI;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 11 step 8: <c>RagCitations.Build</c> is a total function over the <b>complete</b> answer,
/// and every returned span indexes that same string. The slicing assertions are the point - an
/// offset that is plausible but wrong is invisible to any assertion that only counts annotations.
/// </summary>
public class RagCitationsBuildTests
{
    private static IReadOnlyList<RagSource> FiveStampedSources() =>
    [
        .. Enumerable.Range(1, 5).Select(n => new RagSource($"chunk-{n}", $"Body of chunk {n}.")
        {
            Title = $"Chunk {n}",
            Uri = new Uri($"https://example.com/{n}"),
            Ordinal = n,
        }),
    ];

    [Fact]
    public void EverySpanSlicesTheAnswerBackToItsMarkerTextExactly()
    {
        const string Answer = "Coverage runs 24 months [1]. Returns take 30 days [2].";

        var citations = RagCitations.Build(Answer, FiveStampedSources());

        Assert.Equal(2, citations.Count);
        foreach (var citation in citations)
        {
            var expectedMarker = "[" + citation.FileId!.Split('-')[1] + "]";
            foreach (var region in citation.AnnotatedRegions!.Cast<TextSpanAnnotatedRegion>())
            {
                Assert.NotNull(region.StartIndex);
                Assert.NotNull(region.EndIndex);
                Assert.Equal(expectedMarker, Answer[region.StartIndex.Value..region.EndIndex.Value]);
            }
        }
    }

    [Fact]
    public void ADuplicateMarkerProducesOneAnnotationWithTwoRegions()
    {
        const string Answer = "Coverage runs 24 months [1], and the same clause [1] covers parts.";

        var citations = RagCitations.Build(Answer, FiveStampedSources());

        var citation = Assert.Single(citations);
        Assert.Equal(2, citation.AnnotatedRegions!.Count);
        Assert.Equal(
            Answer.IndexOf("[1]", StringComparison.Ordinal),
            ((TextSpanAnnotatedRegion)citation.AnnotatedRegions[0]).StartIndex);
        Assert.Equal(
            Answer.LastIndexOf("[1]", StringComparison.Ordinal),
            ((TextSpanAnnotatedRegion)citation.AnnotatedRegions[1]).StartIndex);
    }

    [Fact]
    public void AMarkerWithNoMatchingSourceIsLeftAsPlainTextAndCountedAndNeverFabricated()
    {
        const string Answer = "Coverage runs 24 months [1], and the appendix says otherwise [7].";

        var citations = RagCitations.Build(Answer, FiveStampedSources());

        var citation = Assert.Single(citations);
        Assert.Equal("chunk-1", citation.FileId);
        Assert.DoesNotContain(citations, c => c.FileId == "chunk-7");
        Assert.Contains("[7]", Answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AMarkerInsideACodeFenceIsTreatedLikeAnyOtherMarkerBecauseTheScanIsLexical()
    {
        const string Answer = "```\nvar x = list[2];\n```\nSee [2].";

        var citations = RagCitations.Build(Answer, FiveStampedSources());

        var citation = Assert.Single(citations);
        Assert.Equal("chunk-2", citation.FileId);
        Assert.Equal(2, citation.AnnotatedRegions!.Count);
    }

    [Fact]
    public void AnAnswerWithNoMarkersProducesAnEmptyListAndDoesNotThrow()
    {
        Assert.Empty(RagCitations.Build("Nothing in the sources answers that.", FiveStampedSources()));
    }

    [Fact]
    public void BuildIsTotalOverAnEmptyAnswerAndAnEmptySourceList()
    {
        Assert.Empty(RagCitations.Build(string.Empty, FiveStampedSources()));
        Assert.Empty(RagCitations.Build("Answer with [1].", []));
    }

    [Fact]
    public void EachCitationCarriesTheSourcesTitleUrlIdAndSnippet()
    {
        var sources = FiveStampedSources();

        var citation = Assert.Single(RagCitations.Build("See [3].", sources));

        Assert.Equal("Chunk 3", citation.Title);
        Assert.Equal(new Uri("https://example.com/3"), citation.Url);
        Assert.Equal("chunk-3", citation.FileId);
        Assert.Equal("Body of chunk 3.", citation.Snippet);
    }

    [Fact]
    public void CitationsComeBackInFirstOccurrenceOrder()
    {
        var citations = RagCitations.Build("First [4], then [2], then [4] again.", FiveStampedSources());

        Assert.Equal(["chunk-4", "chunk-2"], citations.Select(c => c.FileId));
    }

    [Fact]
    public void AttachToAppendsExactlyOneEmptyTextContentCarryingEveryCitation()
    {
        var citations = RagCitations.Build("See [1] and [2].", FiveStampedSources());
        var update = new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails())]);

        RagCitations.AttachTo(update, citations);

        var carrier = Assert.IsType<TextContent>(update.Contents[^1]);
        Assert.Equal(string.Empty, carrier.Text);
        Assert.Equal(2, carrier.Annotations!.Count);
        Assert.Equal(string.Empty, update.Text);
    }

    [Fact]
    public void AttachToWithNoCitationsAttachesNoCarrierAtAll()
    {
        var update = new ChatResponseUpdate(ChatRole.Assistant, "done");

        RagCitations.AttachTo(update, []);

        Assert.Single(update.Contents);
    }
}
