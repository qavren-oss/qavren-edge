using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 10(a): refuse early, by name - and never refuse a caller for requesting the
/// default behaviour.
/// </summary>
public class OptionRefusalTests
{
    private static ChatMessage[] Ask(string text) => [new ChatMessage(ChatRole.User, text)];

    private static async Task<EdgeChatException> RefusedAsync(ClientHarness harness, ChatOptions? options, ChatMessage[]? messages = null) =>
        await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(messages ?? Ask("hi"), options, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

    [Fact]
    public async Task ToolsIs7107()
    {
        using var harness = ClientHarness.Build();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => 1, "one")] };

        var exception = await RefusedAsync(harness, options).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatToolCallingUnsupported, exception.Code);
        Assert.Contains("Tools", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Session.Generators);
    }

    [Fact]
    public async Task RequireAnyIs7107BecauseARequiredCallCanNeverBeEmitted()
    {
        using var harness = ClientHarness.Build();

        var exception = await RefusedAsync(harness, new ChatOptions { ToolMode = ChatToolMode.RequireAny }).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatToolCallingUnsupported, exception.Code);
        Assert.Contains("RequireAny", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireSpecificIs7107NamingTheFunction()
    {
        using var harness = ClientHarness.Build();

        var exception = await RefusedAsync(harness, new ChatOptions { ToolMode = ChatToolMode.RequireSpecific("lookup") }).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatToolCallingUnsupported, exception.Code);
        Assert.Contains("lookup", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoAndNoneToolModesAreFine()
    {
        using var harness = ClientHarness.Build();

        var auto = await harness.Client.GetResponseAsync(Ask("hi"), new ChatOptions { ToolMode = ChatToolMode.Auto }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var none = await harness.Client.GetResponseAsync(Ask("hi"), new ChatOptions { ToolMode = ChatToolMode.None }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("Hello world.", auto.Text);
        Assert.Equal("Hello world.", none.Text);
    }

    [Fact]
    public async Task NonTextContentIs7108NamingTheContentType()
    {
        using var harness = ClientHarness.Build();
        ChatMessage[] messages = [new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")])];

        var exception = await RefusedAsync(harness, options: null, messages).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatOptionUnsupported, exception.Code);
        Assert.Contains("DataContent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxLengthInSearchOptionsIs7108()
    {
        using var harness = ClientHarness.Build(o => o.SearchOptions["max_length"] = 2048.0);

        var exception = await RefusedAsync(harness, options: null).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatOptionUnsupported, exception.Code);
        Assert.Contains("max_length", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Session.Generators);
    }

    [Fact]
    public async Task AJsonResponseFormatUnderDisabledIs7103()
    {
        using var harness = ClientHarness.Build();

        var exception = await RefusedAsync(harness, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatGuidanceUnavailable, exception.Code);
        Assert.Contains("USE_GUIDANCE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJsonResponseFormatUnderRequireNativeWithAnUnprovenProbeIs7103NamingTheProbeResult()
    {
        using var harness = ClientHarness.Build(
            o => o.Guidance = EdgeGuidancePolicy.RequireNative,
            guidance: EdgeGuidanceProbeResult.NotEnforced);

        var exception = await RefusedAsync(harness, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatGuidanceUnavailable, exception.Code);
        Assert.Contains("NotEnforced", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJsonResponseFormatUnderPreferNativeWithAnUnprovenProbeRunsUnconstrainedAndSaysSo()
    {
        using var harness = ClientHarness.Build(
            o => o.Guidance = EdgeGuidancePolicy.PreferNative,
            guidance: EdgeGuidanceProbeResult.NotEnforced);

        var response = await harness.Client.GetResponseAsync(
            Ask("hi"), new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(harness.Session.Guidance[0]);
        var unhonoured = Assert.IsAssignableFrom<IReadOnlyList<string>>(response.AdditionalProperties![EdgeChatProperties.UnhonouredOptions]);
        Assert.Contains("ResponseFormat", unhonoured);
    }

    [Fact]
    public async Task AJsonResponseFormatWithAnEnforcedProbeSetsGuidanceOnTheGenerator()
    {
        using var harness = ClientHarness.Build(
            o => o.Guidance = EdgeGuidancePolicy.PreferNative,
            guidance: EdgeGuidanceProbeResult.Enforced);

        var response = await harness.Client.GetResponseAsync(
            Ask("hi"), new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var guidance = Assert.NotNull(harness.Session.Guidance[0]);
        Assert.Equal("json_schema", guidance.Type);
        Assert.False(response.AdditionalProperties?.ContainsKey(EdgeChatProperties.UnhonouredOptions) ?? false);
    }

    [Fact]
    public async Task UnhonouredOptionsListsFrequencyPenaltyRatherThanDroppingIt()
    {
        using var harness = ClientHarness.Build();

        var response = await harness.Client.GetResponseAsync(
            Ask("hi"), new ChatOptions { FrequencyPenalty = 0.5f }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var unhonoured = Assert.IsAssignableFrom<IReadOnlyList<string>>(response.AdditionalProperties![EdgeChatProperties.UnhonouredOptions]);
        Assert.Equal(["FrequencyPenalty"], unhonoured);

        // And the turn still ran.
        Assert.Equal("Hello world.", response.Text);
    }

    [Fact]
    public async Task UnhonouredOptionsIsAbsentWhenEverythingWasHonoured()
    {
        using var harness = ClientHarness.Build();

        var response = await harness.Client.GetResponseAsync(Ask("hi"), new ChatOptions { Temperature = 0.2f }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(response.AdditionalProperties?.ContainsKey(EdgeChatProperties.UnhonouredOptions) ?? false);
    }

    public static TheoryData<EdgeGuidancePolicy> Policies() =>
        new(EdgeGuidancePolicy.Disabled, EdgeGuidancePolicy.PreferNative, EdgeGuidancePolicy.RequireNative);

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task TheTextResponseFormatIsHonouredByBeingIgnoredUnderEveryPolicy(EdgeGuidancePolicy policy)
    {
        // Refusing a caller for requesting the default behaviour would be a bug; this is the
        // negative that keeps the over-fire out.
        using var harness = ClientHarness.Build(o => o.Guidance = policy);

        var response = await harness.Client.GetResponseAsync(
            Ask("hi"), new ChatOptions { ResponseFormat = ChatResponseFormat.Text }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("Hello world.", response.Text);
        Assert.Null(harness.Session.Guidance[0]);
        Assert.False(response.AdditionalProperties?.ContainsKey(EdgeChatProperties.UnhonouredOptions) ?? false);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task ANullResponseFormatIsUntouchedUnderEveryPolicy(EdgeGuidancePolicy policy)
    {
        using var harness = ClientHarness.Build(o => o.Guidance = policy);

        var response = await harness.Client.GetResponseAsync(
            Ask("hi"), new ChatOptions { ResponseFormat = null }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("Hello world.", response.Text);
        Assert.Null(harness.Session.Guidance[0]);
    }

    [Fact]
    public async Task ARefusalCountsAsARejectedTurnAndLogs933()
    {
        using var harness = ClientHarness.Build();
        harness.Host.IsAcceptingTurns = false;

        var exception = await RefusedAsync(harness, options: null).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatBusy, exception.Code);
        Assert.Equal(1, harness.Client.Statistics.RejectedTurns);
        Assert.True(harness.Logger.Logged(EdgeChatEventIds.TurnRejected));
    }
}
