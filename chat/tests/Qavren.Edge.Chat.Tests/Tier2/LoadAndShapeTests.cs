using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fixtures;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// Spec 16.2: load, then <see cref="ChatModelInfo"/> reports the geometry read from the fixture's
/// own <c>genai_config.json</c>; a deliberately wrong preset shape is 7008 - both against the
/// real load path with real natives, no seam replaced.
/// </summary>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class LoadAndShapeTests(TinyChatModelFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoadReportsTheGeometryReadFromTheFixturesConfig()
    {
        using var host = fixture.NewHost();

        await host.Host.PreloadAsync(Token).ConfigureAwait(true);

        var info = host.Host.Describe();
        Assert.NotNull(info);

        // The geometry the generator wrote into genai_config.json (spec 16.2's hand-authored
        // config): 2 layers, 2 KV heads, head size 16, vocab 256, context 512, llama.
        Assert.Equal("llama", info.Shape.ModelType);
        Assert.Equal(TinyChatModelFixture.ContextLength, info.Shape.ContextLength);
        Assert.Equal(256, info.Shape.VocabSize);
        Assert.Equal(2, info.Shape.NumHiddenLayers);
        Assert.Equal(2, info.Shape.NumKeyValueHeads);
        Assert.Equal(16, info.Shape.HeadSize);
        Assert.Null(info.Shape.SlidingWindow);
        Assert.Equal(TinyChatModel.ModelOnnxFileName, info.Shape.DecoderFileName);
        Assert.Equal(TinyChatModel.ModelOnnxLength + TinyChatModel.ModelOnnxDataLength, info.Shape.WeightsBytes);

        // What the load learned about the backend, on the real natives. This class runs on the
        // host lane AND on the four device lanes, and spec 16.4 is explicit: each lane RECORDS the
        // execution provider; none asserts one. So the provider list is recorded, not compared.
        Assert.NotNull(info.Backend.Providers);
        JobSummary.Record("tier2-load", "providers", string.Join(", ", info.Backend.Providers));
        Assert.True(info.Backend.ChatTemplateSupported);
        Assert.Equal(ChatTemplateProbe.ModelTemplateFormatter, info.Backend.PromptFormatter);
        Assert.Equal(EdgeGuidanceProbeResult.Unprobed, info.Backend.Guidance);

        // The GenAI version is the CPM pin the Chat.Onnx csproj stamps into its own assembly
        // metadata (plan adjustment 1: the GenAI assembly itself reports 0.0.0.0). Asserted against
        // the same value the public diagnostics block reports - not against a literal, so a package
        // bump does not turn tier 2 red on every lane - and against "(unknown)", the fallback the
        // host substitutes when the metadata is missing.
        var chatBlock = Assert.Single(fixture.Diagnostics.Report().Components, c => c.Name == "Qavren.Edge.Chat.Onnx");
        Assert.Equal(chatBlock.Details["genAiVersion"], info.Backend.GenAiVersion);
        Assert.NotEqual("(unknown)", info.Backend.GenAiVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+", info.Backend.GenAiVersion);
        JobSummary.Record("tier2-load", "genAiVersion", info.Backend.GenAiVersion);

        // The budget's answer for a model smaller than every constant: the full 512.
        Assert.Equal(TinyChatModelFixture.ContextLength, info.ResolvedContextTokens);
        Assert.Equal(TinyChatModelFixture.ContextLength, info.Budget.ContextTokens);

        // Bookkeeping.
        Assert.Equal(TinyChatModelFixture.Preset.Id, info.PresetId);
        Assert.Equal(fixture.ModelDirectory, info.Directory);
        Assert.Equal(TinyChatModelFixture.Preset.Manifest.Files[0].Sha256[..16], info.DirectorySha16);
        Assert.True(info.IsLoaded);
        Assert.Equal(1, info.LoadCount);
        Assert.Equal(0, info.ActiveLeases);
        Assert.True(info.LoadDuration > TimeSpan.Zero);

        // The fixture's session_options declares no intra_op_num_threads (plan adjustment 29).
        Assert.Null(info.IntraOpNumThreads);

        Assert.True(host.Logs.Logged(EdgeChatEventIds.ChatModelLoaded));
        Assert.True(host.Logs.Logged(EdgeChatEventIds.ChatTemplateProbed));
    }

    [Fact]
    public async Task ADeliberatelyWrongPresetShapeIs7008NamingTheField()
    {
        var wrong = TinyChatModelFixture.Preset with
        {
            Shape = TinyChatModelFixture.Preset.Shape with { NumHiddenLayers = 3 },
        };

        using var host = fixture.NewHost(preset: wrong);

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => host.Host.PreloadAsync(Token).AsTask()).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatModelShapeMismatch, exception.Code);
        Assert.Contains("num_hidden_layers", exception.Message, StringComparison.Ordinal);

        // The cross-check fires BEFORE the natives are asked to map anything.
        Assert.Null(host.Host.Describe());
        Assert.False(host.Logs.Logged(EdgeChatEventIds.ChatModelLoaded));
    }

    [Fact]
    public async Task ASecondAcquireReusesTheLoadedModelAndCountsItsLease()
    {
        using var host = fixture.NewHost();

        using (var first = await host.Host.AcquireAsync(Token).ConfigureAwait(true))
        using (var second = await host.Host.AcquireAsync(Token).ConfigureAwait(true))
        {
            Assert.Same(first.Model, second.Model);
            Assert.Equal(2, host.Host.Describe()!.ActiveLeases);
            Assert.Equal(1, host.Host.Describe()!.LoadCount);
        }

        Assert.Equal(0, host.Host.Describe()!.ActiveLeases);
        Assert.True(host.Host.Describe()!.IsLoaded);
    }
}
