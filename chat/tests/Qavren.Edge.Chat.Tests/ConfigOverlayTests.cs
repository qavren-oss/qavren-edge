using System.Text.Json;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fixtures;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 9.1 step 5: <b>exactly one</b> <c>Overlay</c> call, with the document composed in
/// managed code because the native merge semantics are undocumented and sub-project 4 will not bet
/// the memory cap on them.
/// </summary>
public class ConfigOverlayTests
{
    private static JsonElement Compose(string? consumerOverlay, int resolvedContext, bool mobile = false)
    {
        var json = GenAiConfigOverlay.Compose(
            GenAiConfigFixtures.LlamaConfigJson, consumerOverlay, resolvedContext, mobile);

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void TheComposedDocumentIsOneJsonObject()
    {
        var root = Compose(consumerOverlay: null, resolvedContext: 2048);

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("search", out var search));
        Assert.Equal(JsonValueKind.Object, search.ValueKind);
    }

    [Fact]
    public void TheModelsOwnShippedSamplingSurvives()
    {
        // The model's search block is RE-EMITTED rather than replaced, so its shipped do_sample /
        // temperature / top_k / top_p survive whichever way the native call behaves.
        var search = Compose(consumerOverlay: null, resolvedContext: 2048).GetProperty("search");

        Assert.True(search.GetProperty("do_sample").GetBoolean());
        Assert.Equal(0.6, search.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(50, search.GetProperty("top_k").GetInt32());
        Assert.Equal(0.9, search.GetProperty("top_p").GetDouble(), 3);

        // past_present_share_buffer selects the STATIC KV path, where the cache is allocated once to
        // max_length - which is why writing the budget's answer into the config caps it at all.
        Assert.True(search.GetProperty("past_present_share_buffer").GetBoolean());
    }

    [Fact]
    public void MaxLengthIsTheBudgetsResolvedContext()
    {
        // The shipped config declares 4096; the budget said 1536, and the budget wins.
        var search = Compose(consumerOverlay: null, resolvedContext: 1536).GetProperty("search");

        Assert.Equal(1536, search.GetProperty("max_length").GetInt32());
    }

    [Fact]
    public void SubProject4sMaxLengthBeatsAConsumerOverlayThatSetItToo()
    {
        var search = Compose("""{"search":{"max_length":40960,"temperature":0.1}}""", 2048)
            .GetProperty("search");

        Assert.Equal(2048, search.GetProperty("max_length").GetInt32());

        // Everything else the consumer asked for still lands: the merge is a deep merge, not a
        // replacement, so one overridden key does not wipe the section.
        Assert.Equal(0.1, search.GetProperty("temperature").GetDouble(), 3);
        Assert.True(search.GetProperty("do_sample").GetBoolean());
    }

    [Fact]
    public void AConsumerOverlaySectionTheModelDoesNotHaveIsCarriedThrough()
    {
        var root = Compose("""{"model":{"decoder":{"session_options":{"intra_op_num_threads":2}}}}""", 2048);

        Assert.Equal(
            2,
            root.GetProperty("model").GetProperty("decoder")
                .GetProperty("session_options").GetProperty("intra_op_num_threads").GetInt32());
    }

    [Fact]
    public void ANonCpuProviderOnAMobileTargetFrameworkIs7009NamingTheProvider()
    {
        var exception = Assert.Throws<EdgeChatException>(() => GenAiConfigOverlay.Compose(
            GenAiConfigFixtures.LlamaConfigJson,
            """{"model":{"decoder":{"session_options":{"provider_options":[{"cuda":{}}]}}}}""",
            2048,
            mobileTargetFramework: true));

        Assert.Equal(EdgeErrorCode.ChatExecutionProviderUnsupported, exception.Code);
        Assert.Contains("cuda", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.Remediation);
    }

    [Fact]
    public void ACpuProviderOnAMobileTargetFrameworkIsFine()
    {
        var root = GenAiConfigOverlay.Compose(
            GenAiConfigFixtures.LlamaConfigJson,
            """{"model":{"decoder":{"session_options":{"provider_options":[{"cpu":{}}]}}}}""",
            2048,
            mobileTargetFramework: true);

        Assert.Contains("cpu", root, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonCpuProviderOffAMobileTargetFrameworkIsTheConsumersChoice()
    {
        // The refusal is scoped to mobile, where no such provider exists in any shipped build.
        var root = GenAiConfigOverlay.Compose(
            GenAiConfigFixtures.LlamaConfigJson,
            """{"model":{"decoder":{"session_options":{"provider_options":[{"cuda":{}}]}}}}""",
            2048,
            mobileTargetFramework: false);

        Assert.Contains("cuda", root, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlayThatIsNotAJsonObjectIs7007()
    {
        var exception = Assert.Throws<EdgeChatException>(() => GenAiConfigOverlay.Compose(
            GenAiConfigFixtures.LlamaConfigJson, "[1,2,3]", 2048, mobileTargetFramework: false));

        Assert.Equal(EdgeErrorCode.ChatConfigurationInvalid, exception.Code);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData("4096")]
    [InlineData(true)]
    public void MaxLengthInSearchOptionsIs7108WhateverItsType(object value)
    {
        var options = new EdgeChatOptions { Preset = ChatPresets.Llama32_1BInstructInt4 };
        options.SearchOptions["max_length"] = value;

        var exception = Assert.Throws<EdgeChatException>(
            () => GenAiConfigOverlay.ThrowIfSearchOptionsDeclareMaxLength(options.SearchOptions));

        Assert.Equal(EdgeErrorCode.ChatOptionUnsupported, exception.Code);
        Assert.Contains("max_length", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MaxContextTokens", exception.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherSearchOptionIsNotRefused()
    {
        var options = new EdgeChatOptions { Preset = ChatPresets.Llama32_1BInstructInt4 };
        options.SearchOptions["min_length"] = 4.0;

        GenAiConfigOverlay.ThrowIfSearchOptionsDeclareMaxLength(options.SearchOptions);
    }
}
