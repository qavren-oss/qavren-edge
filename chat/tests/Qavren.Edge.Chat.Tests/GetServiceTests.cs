using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// <c>GetService</c> is the entire integration surface for Semantic Kernel, Agent Framework and
/// MEAI's own <c>GetRequiredService</c>, so it is asserted as a contract.
/// </summary>
public class GetServiceTests
{
    [Fact]
    public void ReturnsThisForItsOwnTypeAndForIChatClient()
    {
        using var harness = ClientHarness.Build();

        Assert.Same(harness.Client, harness.Client.GetService(typeof(IChatClient)));
        Assert.Same(harness.Client, harness.Client.GetService(typeof(EdgeChatClient)));
        Assert.Same(harness.Client, harness.Client.GetService<IChatClient>());
    }

    [Fact]
    public void ChatClientMetadataCarriesTheProviderNameAndANonNullDefaultModelId()
    {
        // Semantic Kernel's GetModelId() is literally GetService<ChatClientMetadata>()?.DefaultModelId.
        using var harness = ClientHarness.Build();

        var metadata = Assert.IsType<ChatClientMetadata>(harness.Client.GetService(typeof(ChatClientMetadata)));

        Assert.Equal("onnxruntime-genai", metadata.ProviderName);
        Assert.NotNull(metadata.DefaultModelId);
        Assert.Equal(harness.Client.ModelId, metadata.DefaultModelId);
        Assert.Equal("llama-3.2-1b-instruct-int4", metadata.DefaultModelId);
    }

    [Fact]
    public void ChatModelInfoStatisticsAndTheHostResolve()
    {
        using var harness = ClientHarness.Build();

        var info = Assert.IsType<ChatModelInfo>(harness.Client.GetService(typeof(ChatModelInfo)));
        Assert.Same(harness.Host.Info, info);
        Assert.Same(info, harness.Client.Model);

        var statistics = Assert.IsType<ChatClientStatistics>(harness.Client.GetService(typeof(ChatClientStatistics)));
        Assert.Equal(0, statistics.Turns);
        Assert.Null(statistics.TokensPerSecondP50);

        Assert.Same(harness.Host, harness.Client.GetService(typeof(IChatModelHost)));
    }

    [Fact]
    public void ANonNullServiceKeyResolvesNothing()
    {
        using var harness = ClientHarness.Build();

        Assert.Null(harness.Client.GetService(typeof(IChatClient), "keyed"));
        Assert.Null(harness.Client.GetService(typeof(ChatClientMetadata), "keyed"));
        Assert.Null(harness.Client.GetService(typeof(ChatModelInfo), "keyed"));
    }

    [Fact]
    public void AnUnrelatedTypeResolvesNothingAndANullTypeThrows()
    {
        using var harness = ClientHarness.Build();

        Assert.Null(harness.Client.GetService(typeof(FunctionInvokingChatClient)));
        Assert.Null(harness.Client.GetService(typeof(string)));
        Assert.Throws<ArgumentNullException>(() => harness.Client.GetService(null!));
    }

    [Fact]
    public void TheNativeObjectsResolveOnlyWhileTheClientHoldsALease()
    {
        // No lease yet: nothing native is handed out, because a model the host may dispose under
        // the caller is worse than a null.
        using var harness = ClientHarness.Build();

        Assert.Null(harness.Client.GetService(typeof(Model)));
        Assert.Null(harness.Client.GetService(typeof(Tokenizer)));
        Assert.Null(harness.Client.GetService(typeof(Config)));
    }

    [Fact]
    public async Task StatisticsFoldEveryTurnIntoTheBoundedRing()
    {
        using var harness = ClientHarness.Build(o => o.EnableConversationCache = false, script: ["a", "b", "c"]);

        for (var i = 0; i < 70; i++)
        {
            await harness.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var statistics = harness.Client.Statistics;
        Assert.Equal(70, statistics.Turns);
        Assert.Equal(210, statistics.TokensGenerated);
        Assert.NotNull(statistics.TokensPerSecondP50);
        Assert.NotNull(statistics.TokensPerSecondP95);
        Assert.True(statistics.TokensPerSecondP95 >= statistics.TokensPerSecondP50);
        Assert.NotNull(statistics.LastTokensPerSecond);
        Assert.NotNull(statistics.LastTimeToFirstToken);
        Assert.Equal(EdgeChatStopReason.Completed, statistics.LastStopReason);
        Assert.Equal(0, statistics.RejectedTurns);
        Assert.Equal(0, statistics.UnloadEvents);
    }

    [Fact]
    public void TheRegisteredClientIsAnEdgeChatClientWhoseMetadataSemanticKernelCanRead()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddOnnxChat(ChatPresets.Qwen3_600MInt4));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();

        Assert.IsType<EdgeChatClient>(client);
        Assert.Equal("qwen3-0.6b-int4", client.GetService<ChatClientMetadata>()?.DefaultModelId);
        Assert.NotNull(client.GetService<IChatModelHost>());
    }
}
