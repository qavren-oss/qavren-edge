using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>Spec sections 4.3 and 6.9: what <c>AddOnnxChat</c> registers, and how many of it.</summary>
public class RegistrationTests
{
    /// <summary>
    /// A stand-in for the MEAI middleware a real app puts in the pipeline.
    /// </summary>
    /// <remarks>
    /// Spec section 4.3 uses <c>UseRag()</c> here, which lives in <c>Qavren.Edge.Rag</c> - a package
    /// this one never references, and which this test assembly therefore cannot see. The assertion
    /// is about the seam, not about the middleware: that <c>pipeline</c> is invoked against the
    /// builder MEAI's <c>AddChatClient</c> returned, and that the composed pipeline resolves.
    /// </remarks>
    private sealed class MarkerChatClient(IChatClient inner) : DelegatingChatClient(inner);

    [Fact]
    public void TheUnkeyedRegistrationResolvesAClientAHostAndAProvisioner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IChatClient>());
        Assert.NotNull(provider.GetRequiredService<IChatModelHost>());
        Assert.NotNull(provider.GetRequiredService<IChatModelProvisioner>());
        Assert.Single(provider.GetServices<IEdgeLifecycleObserver>(), o => o is ChatLifecycleObserver);
    }

    [Fact]
    public void TheOptionsCallbackConfiguresThisRegistrationAndCannotReplaceThePreset()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddOnnxChat(
            ChatPresets.Llama32_1BInstructInt4,
            o =>
            {
                Assert.Equal(ChatPresets.Llama32_1BInstructInt4, o.Preset);
                o.MaxContextTokens = 2048;
                o.Preset = ChatPresets.Qwen3_600MInt4;
            }));

        using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IChatModelHost>();

        var concrete = Assert.IsType<ChatModelHost>(host);
        Assert.Equal(2048, concrete.Options.MaxContextTokens);

        // Replacing it is unsupported, and "unsupported" means it does not take effect rather than
        // that it silently repoints a 1.24 GB download at a different licence.
        Assert.Equal(ChatPresets.Llama32_1BInstructInt4, concrete.Preset);
        Assert.Equal(ChatPresets.Llama32_1BInstructInt4, concrete.Options.Preset);
    }

    [Fact]
    public void AKeyedAndAnUnkeyedRegistrationCoexist()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4)
            .AddOnnxChat("small", ChatPresets.Qwen3_600MInt4));

        using var provider = services.BuildServiceProvider();

        var unkeyed = Assert.IsType<ChatModelHost>(provider.GetRequiredService<IChatModelHost>());
        var keyed = Assert.IsType<ChatModelHost>(provider.GetRequiredKeyedService<IChatModelHost>("small"));

        Assert.Equal("llama-3.2-1b-instruct-int4", unkeyed.Preset.Id);
        Assert.Equal("qwen3-0.6b-int4", keyed.Preset.Id);
        Assert.NotNull(provider.GetRequiredService<IChatClient>());
        Assert.NotNull(provider.GetRequiredKeyedService<IChatClient>("small"));

        // One process-wide runtime task, however many registrations.
        Assert.Single(
            provider.GetServices<IEdgeStartupTask>(),
            t => t.Order == EdgeChatStartupOrder.ChatEnvironment);
    }

    [Fact]
    public void CallingAddOnnxChatTwiceForOneKeyRegistersOneOfEverything()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4)
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IChatModelHost>());
        Assert.Single(provider.GetServices<IChatModelProvisioner>());
        Assert.Single(provider.GetServices<IEdgeLifecycleObserver>(), o => o is ChatLifecycleObserver);
    }

    [Fact]
    public void ThePipelineCallbackBuildsAndTheComposedClientResolves()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddOnnxChat(
            ChatPresets.Llama32_1BInstructInt4,
            pipeline: chat => chat.Use(static inner => new MarkerChatClient(inner))));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();

        // MEAI's ordering contract: Build applies factories in REVERSE, so the FIRST Use() call is
        // the OUTERMOST client.
        Assert.IsType<MarkerChatClient>(client);
        Assert.False(Microsoft.ML.OnnxRuntime.OrtEnv.IsCreated);
    }

    [Fact]
    public void TheSection43SnippetResolvesInBothBuilderCallOrders()
    {
        // The order of AddOnnxChat and the middleware's own registration is irrelevant, because
        // AddChatClient registers a descriptor whose factory is ChatClientBuilder.Build - so the
        // whole pipeline is constructed lazily from the app's provider.
        var chatFirst = new ServiceCollection();
        chatFirst.AddLogging();
        chatFirst.AddQavrenEdge(edge => edge
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4, pipeline: c => c.Use(static i => new MarkerChatClient(i))));
        chatFirst.AddSingleton(new MarkerDependency());

        var chatLast = new ServiceCollection();

        chatLast.AddLogging();

        chatLast.AddSingleton(new MarkerDependency());
        chatLast.AddQavrenEdge(edge => edge
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4, pipeline: c => c.Use(static i => new MarkerChatClient(i))));

        using var first = chatFirst.BuildServiceProvider();
        using var last = chatLast.BuildServiceProvider();

        Assert.IsType<MarkerChatClient>(first.GetRequiredService<IChatClient>());
        Assert.IsType<MarkerChatClient>(last.GetRequiredService<IChatClient>());
    }

    [Fact]
    public void GetServiceReachesTheMetadataSemanticKernelReadsAndTheHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();

        var metadata = client.GetService<ChatClientMetadata>();
        Assert.Equal("onnxruntime-genai", metadata?.ProviderName);

        // Semantic Kernel's GetModelId() is literally GetService<ChatClientMetadata>()?.DefaultModelId.
        Assert.Equal("llama-3.2-1b-instruct-int4", metadata?.DefaultModelId);
        Assert.NotNull(client.GetService<IChatModelHost>());
        Assert.Null(client.GetService<ChatClientMetadata>("some-key"));
    }

    [Fact]
    public void ANullPresetIsRefusedAtCompositionRatherThanAtTheFirstTurn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddQavrenEdge(edge => edge.AddOnnxChat(preset: null!)));

        Assert.Throws<ArgumentException>(() =>
            services.AddQavrenEdge(edge => edge.AddOnnxChat("   ", ChatPresets.Llama32_1BInstructInt4)));
    }

    private sealed class MarkerDependency;
}
