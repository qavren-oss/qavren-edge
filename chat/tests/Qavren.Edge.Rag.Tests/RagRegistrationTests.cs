using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Rag.Tests.Fakes;
using Xunit;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 7's registration surface on a <b>bare</b> <c>ServiceCollection</c> - no MAUI, no host, no
/// vector store unless the test puts one there. The <c>storeName</c> arms are the half spec 7
/// declares and no earlier task exercised.
/// </summary>
public class RagRegistrationTests
{
    private static ChatMessage[] Question() => [new ChatMessage(ChatRole.User, "How long is the warranty?")];

    private static RagSource Project(TestRecord record) =>
        new(record.Key, record.Text) { Title = record.Title };

    private static FakeVectorStore StoreOf(string collectionName, params string[] keys) =>
        new(new FakeVectorCollection(
            collectionName,
            keys.Select((k, i) => new FakeRow(
                new TestRecord { Key = k, Text = k + " body", Title = k }, 0.1 * (i + 1), 0.01 * (i + 1)))));

    [Fact]
    public void AddEdgeRagOnABareServiceCollectionRegistersOptionsAndTheContributor()
    {
        var services = new ServiceCollection();
        services.AddQavrenEdge(builder => builder.AddEdgeRag(o => o.Top = 9));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(9, provider.GetRequiredService<IOptions<RagOptions>>().Value.Top);
        Assert.Contains(
            provider.GetServices<IEdgeDiagnosticsContributor>(),
            c => c.ComponentName == "Qavren.Edge.Rag");
        Assert.Null(provider.GetService<IEdgeRetriever>());
        Assert.Null(provider.GetService<IChatClient>());
    }

    [Fact]
    public void ADiagnosticsReadThatThrowsRendersAsTheUnavailableSentinelNotAsNull()
    {
        var services = new ServiceCollection();
        services.AddQavrenEdge(builder => builder
            .AddEdgeRag()
            .AddRetriever(_ => new ThrowingNameRetriever()));

        using var provider = services.BuildServiceProvider();
        var contributor = Assert.Single(
            provider.GetServices<IEdgeDiagnosticsContributor>(),
            c => c.ComponentName == "Qavren.Edge.Rag");

        var details = contributor.Describe();

        // Spec 14.3: a read that throws is "(unavailable: <TypeName>)". Null is reserved for
        // "nothing registered" - here the retriever IS registered and the read failed, and a
        // report reader has to be able to tell those two apart.
        Assert.Equal("(unavailable: InvalidTimeZoneException)", details["retriever"]);
        Assert.Equal(typeof(ThrowingNameRetriever).FullName, details["retrieverType"]);
        Assert.Null(details["collectionName"]);

        // The other frame, and the realistic one: the *resolution* throws, not a property on an
        // already-resolved service. A factory that fails must not make the whole block read "not
        // registered" - every value taken through that resolution is the sentinel, including
        // chatClientPresent, where a "False" would be an outright false statement.
        var throwingResolves = new ServiceCollection();
        throwingResolves.AddQavrenEdge(builder => builder
            .AddEdgeRag()
            .AddRetriever(_ => throw new InvalidTimeZoneException("the retriever factory fails here")));
        throwingResolves.AddSingleton<IChatClient>(
            _ => throw new BadImageFormatException("the chat client factory fails here"));

        using var throwingProvider = throwingResolves.BuildServiceProvider();
        var throwingDetails = Assert.Single(
                throwingProvider.GetServices<IEdgeDiagnosticsContributor>(),
                c => c.ComponentName == "Qavren.Edge.Rag")
            .Describe();

        Assert.Equal("(unavailable: InvalidTimeZoneException)", throwingDetails["retriever"]);
        Assert.Equal("(unavailable: InvalidTimeZoneException)", throwingDetails["retrieverType"]);
        Assert.Equal("(unavailable: InvalidTimeZoneException)", throwingDetails["collectionName"]);
        Assert.Equal("(unavailable: InvalidTimeZoneException)", throwingDetails["preferHybridSearch"]);
        Assert.Equal("(unavailable: BadImageFormatException)", throwingDetails["chatClientPresent"]);
        Assert.Equal("(unavailable: BadImageFormatException)", throwingDetails["asks"]);
        Assert.Equal("(unavailable: BadImageFormatException)", throwingDetails["lastGrounded"]);
        Assert.Equal("(unavailable: BadImageFormatException)", throwingDetails["extractiveAnswers"]);

        // The options read is independent of both, so it still reports a real value: the sentinel
        // is per-read, not per-block.
        Assert.Equal("5", throwingDetails["top"]);
    }

    private sealed class ThrowingNameRetriever : IEdgeRetriever
    {
        public string Name => throw new InvalidTimeZoneException("diagnostics read fails here");

        public ValueTask<IReadOnlyList<RagSource>> RetrieveAsync(
            string query, RetrievalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RagSource>>([]);
    }

    [Fact]
    public void UseRagWithNoRetrieverResolvableThrows7201NamingBothRegistrations()
    {
        using var leaf = new FakeChatClient("unused");
        var builder = new ChatClientBuilder(leaf).UseRag();

        var exception = Assert.Throws<EdgeRagException>(() => builder.Build());

        Assert.Equal(EdgeErrorCode.RagRetrieverMissing, exception.Code);
        Assert.Contains("AddVectorStoreRetriever", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddRetriever", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddExtractiveChatWithAUseRagPipelineResolvesAClientThatAnswersWithCitations()
    {
        var services = new ServiceCollection();
        services.AddQavrenEdge(builder => builder
            .AddRetriever(_ => new FakeRetriever(TestCorpus.Two()))
            .AddExtractiveChat(pipeline: c => c.UseRag()));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();

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
    }

    [Fact]
    public void GetServiceReachesTheLeafsMetadataThroughTheRagChatClientStack()
    {
        var services = new ServiceCollection();
        services.AddQavrenEdge(builder => builder
            .AddRetriever(_ => new FakeRetriever(TestCorpus.Two()))
            .AddExtractiveChat(pipeline: c => c.UseRag()));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();

        Assert.NotNull(client.GetService<RagChatClient>());
        Assert.NotNull(client.GetService<IEdgeRetriever>());
        Assert.NotNull(client.GetService<RagOptions>());
        Assert.Equal("qavren.edge.rag.extractive", client.GetService<ChatClientMetadata>()?.ProviderName);

        // MEAI's DelegatingChatClient convention: a serviceType the client itself satisfies
        // returns the client, and everything else delegates inward.
        Assert.Same(client, client.GetService<object>());
        Assert.Same(client, client.GetService<IChatClient>());
    }

    [Fact]
    public void GetServiceDoesNotHandBackTheRetrieverForAServiceItMerelyAlsoImplements()
    {
        using var leaf = new FakeChatClient("leaf answer");
        var retriever = new ChatCapableRetriever();
        using var client = new ChatClientBuilder(leaf).UseRag(retriever).Build();

        Assert.Same(retriever, client.GetService<IEdgeRetriever>());

        // Spec 7's three arms are this, IEdgeRetriever and RagOptions; everything else delegates
        // inward. This retriever also implements IChatClient, so an arm matching "anything the
        // retriever is assignable to" would return it here and quietly break "GetService reaches
        // the leaf" - the guarantee the override exists to protect.
        Assert.Same(client, client.GetService<IChatClient>());
        Assert.Equal("fake-leaf", client.GetService<ChatClientMetadata>()?.ProviderName);
    }

    private sealed class ChatCapableRetriever : IEdgeRetriever, IChatClient
    {
        public string Name => "chat-capable";

        public ValueTask<IReadOnlyList<RagSource>> RetrieveAsync(
            string query, RetrievalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RagSource>>([]);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The retriever must never be asked to answer.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The retriever must never be asked to answer.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    [Fact]
    public async Task AddVectorStoreRetrieverWithANullStoreNameResolvesTheUnkeyedStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MEVD.VectorStore>(StoreOf("notes", "unkeyed-chunk"));
        services.AddKeyedSingleton<MEVD.VectorStore>("notes-store", StoreOf("notes", "keyed-chunk"));
        services.AddQavrenEdge(builder =>
            builder.AddVectorStoreRetriever<string, TestRecord>("notes", Project));

        using var provider = services.BuildServiceProvider();
        var retriever = provider.GetRequiredService<IEdgeRetriever>();

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 5 }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(["unkeyed-chunk"], sources.Select(s => s.Id));
    }

    [Fact]
    public async Task AddVectorStoreRetrieverWithAStoreNameResolvesTheKeyedStore()
    {
        // The two stores hold DIFFERENT records on purpose: a silent fall-through to the unkeyed
        // one would otherwise be green.
        var services = new ServiceCollection();
        services.AddSingleton<MEVD.VectorStore>(StoreOf("notes", "unkeyed-chunk"));
        services.AddKeyedSingleton<MEVD.VectorStore>("notes-store", StoreOf("notes", "keyed-chunk"));
        services.AddQavrenEdge(builder =>
            builder.AddVectorStoreRetriever<string, TestRecord>("notes", Project, storeName: "notes-store"));

        using var provider = services.BuildServiceProvider();
        var retriever = provider.GetRequiredService<IEdgeRetriever>();

        var sources = await retriever
            .RetrieveAsync("warranty?", new RetrievalRequest { Top = 5 }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(["keyed-chunk"], sources.Select(s => s.Id));
    }

    [Fact]
    public void AMissingKeyedStoreFailsAtResolveTimeNamingTheKeyRatherThanAtRegistrationTime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MEVD.VectorStore>(StoreOf("notes", "unkeyed-chunk"));

        // Registration itself must NOT throw: the resolution is lazy, and pretending otherwise
        // would mis-describe when the app breaks.
        services.AddQavrenEdge(builder =>
            builder.AddVectorStoreRetriever<string, TestRecord>("notes", Project, storeName: "missing"));

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<EdgeRagException>(() => provider.GetRequiredService<IEdgeRetriever>());

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }
}
