using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The history reducer budgets the messages' text; the chat template then wraps each message in
/// role markers the budget never saw. <c>ReservedPromptTokens</c> is a guess at that overhead, and
/// when the guess was short the turn was refused with 7102 while older turns were still there to
/// drop.
/// </summary>
/// <remarks>
/// The fake template is <c>&lt;role&gt;content&lt;/role&gt;\n</c> per message plus a trailing
/// <c>&lt;assistant&gt;</c>, one token per character: 14 characters of overhead on a user message,
/// 24 on an assistant one, 11 at the end. Every message below is exactly 25 characters.
/// </remarks>
public class TemplateOverheadTests
{
    private static ChatMessage[] FiveMessages() =>
    [
        new(ChatRole.User, "first question".PadRight(25, '.')),
        new(ChatRole.Assistant, "first answer".PadRight(25, '.')),
        new(ChatRole.User, "second question".PadRight(25, '.')),
        new(ChatRole.Assistant, "second answer".PadRight(25, '.')),
        new(ChatRole.User, "third question".PadRight(25, '.')),
    ];

    [Fact]
    public async Task APromptTheTemplatePushesOverTheContextDropsTheOldestTurnInsteadOfFailing()
    {
        // 200 of context, 20 for the answer, nothing reserved: 180 for history text, and the 125
        // characters of history fit it. Formatted they are 226, and 226 + 20 does not fit 200.
        // Dropping the oldest exchange leaves 138 + 20, which does.
        using var harness = ClientHarness.Build(
            o =>
            {
                o.ReservedPromptTokens = 0;
                o.MaxOutputTokens = 20;
            },
            resolvedContext: 200);

        var response = await harness.Client.GetResponseAsync(FiveMessages(), null, TestContext.Current.CancellationToken);

        Assert.Equal("Hello world.", response.Text);

        var status = response.GetTurnStatus()!;
        Assert.Equal(2, status.MessagesDropped);
        Assert.Equal(138, status.PromptTokens);

        // Formatted twice: once over the history the text budget kept, once over what fits.
        Assert.Equal(2, harness.Session.TemplateCalls.Count);
        var prompt = harness.Session.TemplateCalls[^1];
        Assert.DoesNotContain("first question", prompt, StringComparison.Ordinal);
        Assert.Contains("second question", prompt, StringComparison.Ordinal);
        Assert.Contains("third question", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheFloorIsReachedItIsStillSevenOneZeroTwo()
    {
        // 120 of context: once the oldest exchange is gone the newest two messages are the floor
        // (MinimumPreservedMessages), and 138 + 20 still does not fit. That is the refusal the
        // floor exists to make, and it is made with the real prompt size.
        using var harness = ClientHarness.Build(
            o =>
            {
                o.ReservedPromptTokens = 0;
                o.MaxOutputTokens = 20;
            },
            resolvedContext: 120);

        var refused = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(FiveMessages(), null, TestContext.Current.CancellationToken));

        Assert.Equal(EdgeErrorCode.ChatPromptTooLong, refused.Code);
        Assert.Equal(138, refused.PromptTokens);
        Assert.Equal(0, harness.Host.ActiveLeases);
    }
}
