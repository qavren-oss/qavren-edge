using Microsoft.Extensions.AI;
using Qavren.Edge.Rag.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// The no-LLM floor. The last test is the whole point of the package: a cited answer with no model,
/// no natives and no weights - proven rather than asserted.
/// </summary>
public class ExtractiveChatClientTests
{
    private static ChatMessage[] Question() => [new ChatMessage(ChatRole.User, "How long is the warranty?")];

    private static ChatOptions WithSources(IReadOnlyList<RagSource> sources) =>
        new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RagCitations.SourcesPropertyKey] = sources,
            },
        };

    private static IReadOnlyList<RagSource> Stamped() =>
        [.. TestCorpus.Two().Select((s, i) => s with { Ordinal = i + 1 })];

    [Fact]
    public async Task ItAnswersFromTheTopSourcesWithSynthesisedMarkersAndTheGroundedFlagSet()
    {
        using var client = new ExtractiveChatClient();

        var response = await client
            .GetResponseAsync(Question(), WithSources(Stamped()), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Contains(TestCorpus.FirstText, response.Text, StringComparison.Ordinal);
        Assert.Contains("[1]", response.Text, StringComparison.Ordinal);
        Assert.Contains("[2]", response.Text, StringComparison.Ordinal);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(true, (bool?)response.AdditionalProperties?[RagCitations.GroundedPropertyKey]);
    }

    [Fact]
    public async Task MaxSourcesCapsHowManyAreQuoted()
    {
        using var client = new ExtractiveChatClient(new ExtractiveChatOptions { MaxSources = 1 });

        var response = await client
            .GetResponseAsync(Question(), WithSources(Stamped()), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Contains("[1]", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[2]", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxCharsPerSourceClampsWithAVisibleTruncationMarker()
    {
        using var client = new ExtractiveChatClient(new ExtractiveChatOptions { MaxCharsPerSource = 8 });

        var response = await client
            .GetResponseAsync(Question(), WithSources(Stamped()), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Contains(RagPrompts.TruncationSuffix, response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokedBareWithNoSourcesItAnswersNoResultsWithTheFlagClearAndDoesNotThrow()
    {
        using var client = new ExtractiveChatClient();

        var response = await client
            .GetResponseAsync(Question(), cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(RagPrompts.DefaultNoContextAnswer, response.Text);
        Assert.Equal(false, (bool?)response.AdditionalProperties?[RagCitations.GroundedPropertyKey]);
    }

    [Fact]
    public async Task TheStreamingPathYieldsTheSameAnswerAsTheAggregatedOne()
    {
        using var client = new ExtractiveChatClient();
        var options = WithSources(Stamped());

        var streamed = await client
            .GetStreamingResponseAsync(Question(), options, TestContext.Current.CancellationToken)
            .ToChatResponseAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var aggregated = await client
            .GetResponseAsync(Question(), options, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(aggregated.Text, streamed.Text);
    }

    [Fact]
    public async Task UseRagOverTheExtractiveFloorProducesACitedAnswerWithNoModel()
    {
        using var floor = new ExtractiveChatClient();
        using var client = new ChatClientBuilder(floor)
            .UseRag(new FakeRetriever(TestCorpus.Two()))
            .Build();

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
        Assert.Equal(["chunk-a", "chunk-b"], citations.Select(c => c.FileId));
        Assert.Contains(TestCorpus.FirstText, response.Text, StringComparison.Ordinal);

        foreach (var citation in citations)
        {
            foreach (var region in citation.AnnotatedRegions!.Cast<TextSpanAnnotatedRegion>())
            {
                var slice = response.Text[region.StartIndex!.Value..region.EndIndex!.Value];
                Assert.Matches(@"^\[\d+\]$", slice);
            }
        }
    }

    [Fact]
    public void GetServiceReachesTheMetadataAndTheOptions()
    {
        using var client = new ExtractiveChatClient();

        Assert.Equal("qavren.edge.rag.extractive", client.GetService<ChatClientMetadata>()?.ProviderName);
        Assert.NotNull(client.GetService<ExtractiveChatOptions>());
        Assert.Same(client, client.GetService<IChatClient>());
    }
}
