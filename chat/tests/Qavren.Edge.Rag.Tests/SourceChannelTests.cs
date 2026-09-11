using Microsoft.Extensions.AI;
using Qavren.Edge.Rag.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 11 step 6's second carrier - the channel the no-LLM floor depends on. Without these
/// assertions <c>ExtractiveChatClientTests</c> proves nothing about how a real inner client is fed.
/// </summary>
public class SourceChannelTests
{
    private static ChatMessage[] Question() => [new ChatMessage(ChatRole.User, "How long is the warranty?")];

    [Fact]
    public async Task TheKeyIsSetOnTheOptionsPassedInwardAndNotOnTheCallersInstance()
    {
        using var inner = new FakeChatClient("Answer [1].");
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        // Frozen: the caller hands over an options object with NO AdditionalProperties at all, and
        // checks afterwards that it still has none.
        var callerOptions = new ChatOptions { Temperature = 0.4f };

        await client
            .GetResponseAsync(Question(), callerOptions, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Null(callerOptions.AdditionalProperties);
        Assert.NotSame(callerOptions, inner.LastOptions);
        Assert.Equal(0.4f, inner.LastOptions!.Temperature);
        Assert.NotEmpty(inner.LastSources);
    }

    [Fact]
    public async Task AnInnerClientReadsTheListBackWithOrdinalStamped1ToN()
    {
        using var inner = new FakeChatClient("Answer [1].");
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        await client
            .GetResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal([1, 2], inner.LastSources.Select(s => s.Ordinal));
        Assert.Equal(["chunk-a", "chunk-b"], inner.LastSources.Select(s => s.Id));
    }

    [Fact]
    public async Task TheMiddlewareCreatesOptionsWhenTheCallerPassedNone()
    {
        using var inner = new FakeChatClient("Answer [1].");
        using var client = new RagChatClient(inner, new FakeRetriever(TestCorpus.Two()));

        await client
            .GetResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.NotNull(inner.LastOptions);
        Assert.NotEmpty(inner.LastSources);
    }

    [Fact]
    public void TheThreePropertyKeysAreTheDocumentedLiterals()
    {
        Assert.Equal("qavren.edge.rag.sources", RagCitations.SourcesPropertyKey);
        Assert.Equal("qavren.edge.rag.grounded", RagCitations.GroundedPropertyKey);
        Assert.Equal("qavren.edge.rag.context", RagCitations.ContextMessagePropertyKey);
    }
}
