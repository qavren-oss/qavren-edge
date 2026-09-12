using System.Text;
using Microsoft.Extensions.AI;
using Qavren.Edge.Rag.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 11's whole streaming pipeline, over a fake retriever and a fake <c>IChatClient</c>. The
/// middleware is <c>DelegatingChatClient</c> middleware and nothing here knows what the leaf is.
/// </summary>
public class RagChatClientStreamingTests
{
    private const string FirstDelta = "Coverage runs 24 months [1]";
    private const string SecondDelta = ", and returns take 30 days [2].";

    private static ChatMessage[] Question() => [new ChatMessage(ChatRole.User, "How long is the warranty?")];

    private static async Task<List<ChatResponseUpdate>> DrainAsync(
        RagChatClient client, ChatOptions? options = null)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client
            .GetStreamingResponseAsync(Question(), options, TestContext.Current.CancellationToken)
            .ConfigureAwait(true))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static string Accumulate(IEnumerable<ChatResponseUpdate> updates)
    {
        var builder = new StringBuilder();
        foreach (var update in updates)
        {
            builder.Append(update.Text);
        }

        return builder.ToString();
    }

    [Fact]
    public async Task EveryInnerUpdateIsForwardedVerbatimAndTheAnswerIsTheConcatenation()
    {
        using var inner = new FakeChatClient(FirstDelta, SecondDelta);
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        var updates = await DrainAsync(client).ConfigureAwait(true);

        Assert.Equal(3, updates.Count);
        Assert.Equal(FirstDelta, updates[0].Text);
        Assert.Equal(SecondDelta, updates[1].Text);
        Assert.Equal(ChatFinishReason.Stop, updates[2].FinishReason);
        Assert.Equal(FirstDelta + SecondDelta, Accumulate(updates));
    }

    [Fact]
    public async Task TheAnnotationsArriveOnExactlyOneUpdateCarriedByAnEmptyTextContent()
    {
        using var inner = new FakeChatClient(FirstDelta, SecondDelta);
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        var updates = await DrainAsync(client).ConfigureAwait(true);

        var annotated = updates
            .SelectMany(u => u.Contents)
            .OfType<TextContent>()
            .Where(c => c.Annotations is { Count: > 0 })
            .ToList();

        var carrier = Assert.Single(annotated);
        Assert.Empty(carrier.Text);
        Assert.Contains(carrier, updates[^1].Contents);

        // Never on a token update.
        Assert.All(
            updates.Take(updates.Count - 1).SelectMany(u => u.Contents).OfType<TextContent>(),
            content => Assert.True(content.Annotations is null or { Count: 0 }));
    }

    [Fact]
    public async Task EverySpanSlicesTheACCUMULATEDAnswerBackToItsMarkerText()
    {
        using var inner = new FakeChatClient(FirstDelta, SecondDelta);
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        var updates = await DrainAsync(client).ConfigureAwait(true);
        var answer = Accumulate(updates);

        var citations = updates[^1].Contents
            .OfType<TextContent>()
            .Single(c => c.Annotations is { Count: > 0 })
            .Annotations!
            .Cast<CitationAnnotation>()
            .ToList();

        Assert.Equal(2, citations.Count);
        Assert.Equal(["chunk-a", "chunk-b"], citations.Select(c => c.FileId));

        foreach (var citation in citations)
        {
            var ordinal = citation.FileId == "chunk-a" ? "[1]" : "[2]";
            foreach (var region in citation.AnnotatedRegions!.Cast<TextSpanAnnotatedRegion>())
            {
                Assert.Equal(ordinal, answer[region.StartIndex!.Value..region.EndIndex!.Value]);
            }
        }
    }

    [Fact]
    public async Task ARetrievalThrowIsContainedAndTheTurnContinuesUngrounded()
    {
        using var inner = new FakeChatClient("I could not check the sources.");
        var retriever = FakeRetriever.Throwing(new InvalidOperationException("the index is gone"));
        using var client = new RagChatClient(inner, retriever);

        var updates = await DrainAsync(client).ConfigureAwait(true);

        Assert.Equal(1, inner.CallCount);
        Assert.Equal(
            false,
            updates[^1].AdditionalProperties?[RagCitations.GroundedPropertyKey]);

        // No context block reached the model: the conversation the leaf saw is the caller's.
        Assert.Single(inner.LastMessages);
    }

    [Fact]
    public async Task ContinueOnRetrievalFailureFalseRethrowsAs7202()
    {
        using var inner = new FakeChatClient("unused");
        var retriever = FakeRetriever.Throwing(new InvalidOperationException("the index is gone"));
        using var client = new RagChatClient(
            inner, retriever, new RagOptions { ContinueOnRetrievalFailure = false });

        var exception = await Assert.ThrowsAsync<EdgeRagException>(
            async () => await DrainAsync(client).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.RagRetrievalFailed, exception.Code);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task ShortCircuitOnNoContextSpendsZeroInnerClientCalls()
    {
        using var inner = new FakeChatClient("never reached");
        using var client = new RagChatClient(inner, new FakeRetriever([]));

        var updates = await DrainAsync(client).ConfigureAwait(true);

        Assert.Equal(0, inner.CallCount);
        var update = Assert.Single(updates);
        Assert.Equal(RagPrompts.DefaultNoContextAnswer, update.Text);
        Assert.Equal(ChatFinishReason.Stop, update.FinishReason);
        Assert.Equal(false, update.AdditionalProperties?[RagCitations.GroundedPropertyKey]);
    }

    [Fact]
    public async Task ShouldRetrieveReturningFalseIsAStraightPassthrough()
    {
        using var inner = new FakeChatClient("Hello.");
        var retriever = new FakeRetriever(TestCorpus.Two());
        using var client = new RagChatClient(
            inner, retriever, new RagOptions { ShouldRetrieve = (_, _) => false });

        var updates = await DrainAsync(client).ConfigureAwait(true);

        Assert.Equal(0, retriever.CallCount);
        Assert.Equal(1, inner.CallCount);
        Assert.Equal("Hello.", Accumulate(updates));
        Assert.DoesNotContain(
            updates.SelectMany(u => u.Contents).OfType<TextContent>(),
            c => c.Annotations is { Count: > 0 });
    }

    [Fact]
    public async Task TheContextBlockIsInjectedImmediatelyBeforeTheNewestUserMessageAndIsPinned()
    {
        using var inner = new FakeChatClient("Answer [1].");
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        await DrainAsync(client).ConfigureAwait(true);

        Assert.Equal(2, inner.LastMessages.Count);

        var injected = inner.LastMessages[0];
        Assert.Equal(ChatRole.User, injected.Role);
        Assert.Contains(RagPrompts.ContextHeading, injected.Text, StringComparison.Ordinal);
        Assert.Equal(true, injected.AdditionalProperties?[RagCitations.ContextMessagePropertyKey]);
        Assert.Equal("How long is the warranty?", inner.LastMessages[1].Text);
    }

    [Fact]
    public async Task TheRequestCarriesRagOptionsTopAndTheExtractedKeywords()
    {
        using var inner = new FakeChatClient("Answer [1].");
        var retriever = new FakeRetriever(TestCorpus.Two());
        using var client = new RagChatClient(inner, retriever, new RagOptions { Top = 7 });

        await DrainAsync(client).ConfigureAwait(true);

        Assert.Equal("How long is the warranty?", retriever.LastQuery);
        Assert.Equal(7, retriever.LastRequest!.Top);
        Assert.Equal(0, retriever.LastRequest.Skip);
        Assert.Contains("warranty", retriever.LastRequest.Keywords!, StringComparer.Ordinal);
    }

    [Fact]
    public async Task AMarkerWithNoSourceIsDroppedRatherThanFabricated()
    {
        using var inner = new FakeChatClient("Coverage runs 24 months [7].");
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        var updates = await DrainAsync(client).ConfigureAwait(true);

        Assert.DoesNotContain(
            updates.SelectMany(u => u.Contents).OfType<TextContent>(),
            c => c.Annotations is { Count: > 0 });
    }
}
