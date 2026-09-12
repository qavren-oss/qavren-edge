using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Internal;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// Spec 16.2: <c>ApplyChatTemplate</c> round-trips through minja on the real natives, a
/// <see cref="ChatPromptFormatter"/> override replaces it, and the guidance positive-control probe
/// runs with its result <b>printed</b> whichever way it goes.
/// </summary>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class TemplateAndGuidanceTests(TinyChatModelFixture fixture)
{
    /// <summary>What the fixture's Jinja renders for the probe's two messages plus the generation prompt.</summary>
    private const string ProbeRendered = "<|user|>\nping\n<|assistant|>\npong\n<|assistant|>\n";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApplyChatTemplateRoundTripsTheFixturesJinjaThroughMinja()
    {
        using var host = fixture.NewHost();
        using var lease = await host.Host.AcquireAsync(Token).ConfigureAwait(true);

        // The raw tokenizer, exactly as the probe and the turn call it.
        var rendered = lease.Tokenizer.ApplyChatTemplate(null!, ChatTemplateProbe.ProbeMessagesJson, null!, true);
        Assert.Equal(ProbeRendered, rendered);

        // Without the generation prompt, the trailing assistant header is absent.
        var bare = lease.Tokenizer.ApplyChatTemplate(null!, ChatTemplateProbe.ProbeMessagesJson, null!, false);
        Assert.Equal("<|user|>\nping\n<|assistant|>\npong\n", bare);

        Assert.True(lease.Info.Backend.ChatTemplateSupported);
        Assert.Equal(ChatTemplateProbe.ModelTemplateFormatter, lease.Info.Backend.PromptFormatter);
    }

    [Fact]
    public async Task ATurnsPromptIsTheTemplatesOutputForTheReducedMessages()
    {
        using var client = fixture.NewClient(o => o.MaxOutputTokens = 2);

        await client.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], null, Token).ConfigureAwait(true);

        var rendered = Assert.Single(client.Sessions.TemplateOutputs);
        Assert.Equal("<|user|>\nping\n<|assistant|>\n", rendered);
    }

    [Fact]
    public async Task APromptFormatterOverrideReplacesTheTemplate()
    {
        var calls = 0;
        using var client = fixture.NewClient(o =>
        {
            o.MaxOutputTokens = 2;
            o.PromptFormatter = (messages, _, context) =>
            {
                calls++;

                // The context reaches the model's own counter and geometry, never the raw tokenizer.
                Assert.True(context.CountTokens("abc") > 0);
                Assert.Equal(TinyChatModelFixture.ContextLength, context.Shape.ContextLength);
                Assert.Equal(TinyChatModelFixture.ContextLength, context.ResolvedContextTokens);

                return "<|user|>\n" + messages[^1].Text + "\n<|assistant|>\n";
            };
        });

        var response = await client.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], null, Token).ConfigureAwait(true);

        Assert.Equal(1, calls);
        Assert.Equal(2, response.GetTurnStatus()!.GeneratedTokens);

        // The template was never consulted for the turn, and the report says who formatted.
        Assert.Empty(client.Sessions.TemplateOutputs);
        Assert.Equal(ChatTemplateProbe.ConsumerFormatter, client.Client.Model!.Backend.PromptFormatter);
        Assert.True(client.Logs.Logged(EdgeChatEventIds.PromptFormatterOverridden));
    }

    [Fact]
    public async Task TheGuidancePositiveControlProbeRunsAndItsResultIsPrintedWhicheverWayItGoes()
    {
        // This is the single test that answers whether the host's natives enforce constraints. It
        // asserts NOTHING about the answer - that would make the suite red on a platform whose
        // build settings sub-project 4 does not control - only that the probe ran.
        using var host = fixture.NewHost(o => o.Guidance = EdgeGuidancePolicy.PreferNative);

        await host.Host.PreloadAsync(Token).ConfigureAwait(true);

        var verdict = host.Host.Describe()!.Backend.Guidance;
        Assert.NotEqual(EdgeGuidanceProbeResult.Unprobed, verdict);
        Assert.True(host.Logs.Logged(EdgeChatEventIds.GuidanceProbed));

        JobSummary.Record("tier2-guidance", "guidanceEnforced", verdict);
        JobSummary.Record("tier2-guidance", "runtimeIdentifier", RuntimeInformation.RuntimeIdentifier);
        JobSummary.Record("tier2-guidance", "genAiVersion", host.Host.Describe()!.Backend.GenAiVersion);
    }

    [Fact]
    public async Task UnderRequireNativeAJsonRequestIsRefusedWhenTheProbeDidNotProveEnforcement()
    {
        using var client = fixture.NewClient(o =>
        {
            o.Guidance = EdgeGuidancePolicy.RequireNative;
            o.MaxOutputTokens = 2;
        });

        // A plain turn is fine under RequireNative; only a JSON request consults the verdict.
        await client.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], null, Token).ConfigureAwait(true);
        var verdict = client.Client.Model!.Backend.Guidance;

        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json, MaxOutputTokens = 2 };

        if (verdict == EdgeGuidanceProbeResult.Enforced)
        {
            // A guidance-enabled build: the request is honoured and the generator carries it.
            await client.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], options, Token).ConfigureAwait(true);
            Assert.NotNull(client.Sessions.Guidance(client.Sessions.GeneratorsBuilt - 1));
        }
        else
        {
            var refused = await Assert.ThrowsAsync<EdgeChatException>(
                () => client.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], options, Token)).ConfigureAwait(true);
            Assert.Equal(EdgeErrorCode.ChatGuidanceUnavailable, refused.Code);
        }

        JobSummary.Record("tier2-guidance", "requireNativeVerdict", verdict);
    }
}
