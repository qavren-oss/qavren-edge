using System.Text;
using Microsoft.Extensions.AI;
using Qavren.Edge.Rag.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 11 step 8's non-streaming half: one aggregation and exactly one fix-up. The offsets are
/// never recomputed, because the string never changes - the assertions slice
/// <c>ChatResponse.Text</c> with the very spans the streaming path produced.
/// </summary>
public class RagChatClientNonStreamingTests
{
    private const string FirstDelta = "Coverage runs 24 months [1]";
    private const string SecondDelta = ", and returns take 30 days [2].";

    private static ChatMessage[] Question() => [new ChatMessage(ChatRole.User, "How long is the warranty?")];

    [Fact]
    public async Task TheAnnotationsLandOnTheFirstTextContentOfTheAggregatedMessage()
    {
        using var inner = new FakeChatClient(FirstDelta, SecondDelta);
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        var response = await client
            .GetResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var message = Assert.Single(response.Messages);
        var texts = message.Contents.OfType<TextContent>().ToList();
        var first = texts[0];

        Assert.NotNull(first.Annotations);
        Assert.Equal(2, first.Annotations!.Count);
        Assert.Equal(FirstDelta + SecondDelta, first.Text);
    }

    [Fact]
    public async Task TheEmptiedCarrierIsGoneAfterTheFixUp()
    {
        using var inner = new FakeChatClient(FirstDelta, SecondDelta);
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        var response = await client
            .GetResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.DoesNotContain(
            response.Messages.SelectMany(m => m.Contents).OfType<TextContent>(),
            content => content.Text.Length == 0 && content.Annotations is { Count: > 0 });
    }

    [Fact]
    public async Task TheSpansStillSliceChatResponseTextBackToTheirMarkers()
    {
        using var inner = new FakeChatClient(FirstDelta, SecondDelta);
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        var response = await client
            .GetResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var citations = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Where(c => c.Annotations is { Count: > 0 })
            .SelectMany(c => c.Annotations!)
            .Cast<CitationAnnotation>()
            .ToList();

        Assert.Equal(2, citations.Count);

        foreach (var citation in citations)
        {
            var marker = citation.FileId == "chunk-a" ? "[1]" : "[2]";
            foreach (var region in citation.AnnotatedRegions!.Cast<TextSpanAnnotatedRegion>())
            {
                Assert.Equal(marker, response.Text[region.StartIndex!.Value..region.EndIndex!.Value]);
            }
        }
    }

    [Fact]
    public async Task AggregationAloneLeavesTheCitationsOnTheCarrierWhichIsWhyTheFixUpExists()
    {
        // Measured, not assumed: ToChatResponseAsync leaves two TextContents on the message - the
        // full text with NO annotations, and the empty carrier holding all of them. A consumer of
        // ChatResponse would find the citations on the one content whose text the spans do not
        // index. That is exactly the state GetResponseAsync's one fix-up repairs, and this test is
        // what turns the fix-up red if MEAI's aggregation ever starts doing it for us.
        using var inner = new FakeChatClient(FirstDelta, SecondDelta);
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        var raw = await client
            .GetStreamingResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ToChatResponseAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var texts = raw.Messages.SelectMany(m => m.Contents).OfType<TextContent>().ToList();

        Assert.Equal(2, texts.Count);
        Assert.Equal(FirstDelta + SecondDelta, texts[0].Text);
        Assert.True(texts[0].Annotations is null or { Count: 0 });
        Assert.Empty(texts[1].Text);
        Assert.Equal(2, texts[1].Annotations!.Count);
    }

    [Fact]
    public async Task ChatResponseTextIsByteIdenticalToTheStreamedConcatenation()
    {
        using var streamingInner = new FakeChatClient(FirstDelta, SecondDelta);
        using var streamingClient = new RagChatClient(streamingInner, new FakeRetriever(TestCorpus.Two()));

        var streamed = new StringBuilder();
        await foreach (var update in streamingClient
            .GetStreamingResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            streamed.Append(update.Text);
        }

        using var aggregatedInner = new FakeChatClient(FirstDelta, SecondDelta);
        using var aggregatedClient = new RagChatClient(aggregatedInner, new FakeRetriever(TestCorpus.Two()));

        var response = await aggregatedClient
            .GetResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(streamed.ToString(), response.Text);
    }
}
