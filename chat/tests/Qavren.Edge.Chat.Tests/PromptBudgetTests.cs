using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 15.3's 7102 row, which Task 3.1 deferred here: a prompt still over budget after the
/// reducer reached its floor. The fake tokenizer is one token per character, so every number below
/// is a character count.
/// </summary>
public class PromptBudgetTests
{
    private const string PinnedKey = "qavren.edge.rag.context";

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage Pinned(string text) => new(ChatRole.User, text)
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [PinnedKey] = text },
    };

    [Fact]
    public async Task ASingleEnormousUserMessageThatNoReductionCanHelpIs7102CarryingBothNumbers()
    {
        // 128 of context, 32 for the answer: the prompt may be 96 tokens. The template adds
        // 25 characters of scaffolding, so a 200-character message renders to 225 and cannot fit.
        using var harness = ClientHarness.Build(
            o =>
            {
                o.MaxOutputTokens = 32;
                o.ReservedPromptTokens = 0;
            },
            resolvedContext: 128);

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync([User(new string('q', 200))], null, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatPromptTooLong, exception.Code);
        Assert.Equal(225, exception.PromptTokens);
        Assert.Equal(128, exception.RequestedContextTokens);
        Assert.Equal(96, exception.FittingContextTokens);
        Assert.Contains("225", exception.Message, StringComparison.Ordinal);
        Assert.Contains("96", exception.Message, StringComparison.Ordinal);

        Assert.Contains("MaxOutputTokens", exception.Remediation!, StringComparison.Ordinal);
        Assert.Contains("ChatHistoryOptions", exception.Remediation!, StringComparison.Ordinal);
        Assert.Contains("RagOptions.MaxContextTokens", exception.Remediation!, StringComparison.Ordinal);

        // Nothing was built, and the gate was released.
        Assert.Empty(harness.Session.Generators);
        Assert.Equal(0, harness.Host.ActiveLeases);
        Assert.Equal("Hello world.", (await harness.Client.GetResponseAsync([User("ok")], null, TestContext.Current.CancellationToken).ConfigureAwait(true)).Text);
    }

    [Fact]
    public async Task APinnedRagContextBlockBiggerThanTheBudgetIs7102BecauseTheReducerRefusesToEvictTheGrounding()
    {
        using var harness = ClientHarness.Build(
            o =>
            {
                o.MaxOutputTokens = 32;
                o.ReservedPromptTokens = 0;
            },
            resolvedContext: 128);

        ChatMessage[] history =
        [
            User("first"), Assistant("reply"),
            User("second"), Assistant("reply"),
            Pinned(new string('c', 150)),
            User("short question"),
        ];

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(history, null, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatPromptTooLong, exception.Code);
        Assert.NotNull(exception.PromptTokens);

        // The reducer evicted the two old turns and kept the pinned block, so the prompt the
        // client counted holds the block and the question and nothing older.
        var rendered = harness.Session.TemplateCalls[0];
        Assert.DoesNotContain("first", rendered, StringComparison.Ordinal);
        Assert.Contains(new string('c', 150), rendered, StringComparison.Ordinal);
        Assert.Contains("short question", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHistoryThatFitsAfterReductionRunsAndLogs935WithTheCount()
    {
        using var harness = ClientHarness.Build(
            o =>
            {
                o.MaxOutputTokens = 16;
                o.ReservedPromptTokens = 0;
                o.History.MaxTurns = 1;
                o.History.MinimumPreservedMessages = 1;
            },
            resolvedContext: 256);

        ChatMessage[] history =
        [
            User(new string('a', 40)), Assistant(new string('b', 40)),
            User(new string('c', 40)), Assistant(new string('d', 40)),
            User("now"),
        ];

        var response = await harness.Client.GetResponseAsync(history, null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var status = response.GetTurnStatus()!;
        Assert.Equal(EdgeChatStopReason.Completed, status.StopReason);
        // MaxTurns = 1 keeps only the newest eviction group: both older user/assistant pairs go.
        Assert.Equal(4, status.MessagesDropped);

        var reduced = Assert.Single(harness.Logger.For(EdgeChatEventIds.HistoryReduced), r => r.Level > Microsoft.Extensions.Logging.LogLevel.Trace);
        Assert.Contains("4 of 5", reduced.Message, StringComparison.Ordinal);
        Assert.Equal(1, harness.Client.Statistics.HistoryReductions);
    }

    [Fact]
    public async Task ACachedGeneratorThatCannotHoldTheTurnIsAMissAndARebuildNeverA7102()
    {
        // Turn one: a long answer fills the cached generator. Turn two asks for a bigger answer
        // than the cached sequence leaves room for, and its reduction drops turn one entirely -
        // so the cached generator cannot hold it, the fresh render fits, and the turn rebuilds.
        var script = Enumerable.Range(0, 40).Select(_ => "x").ToArray();

        using var harness = ClientHarness.Build(
            o =>
            {
                o.MaxOutputTokens = 40;
                o.ReservedPromptTokens = 0;
                o.History.MaxTurns = 1;
                o.History.MinimumPreservedMessages = 1;
            },
            resolvedContext: 140,
            script: script);

        var first = await harness.Client.GetResponseAsync([User("one")], null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var id = first.ConversationId!;
        var cached = harness.Session.LastGenerator;

        // Held: 28 prompt + 40 generated = 68. Another 80 of output cannot fit in 140.
        Assert.Equal(68UL, cached.TokenCount());

        ChatMessage[] history = [User("one"), Assistant(first.Text), User("two")];
        var second = await harness.Client.GetResponseAsync(
            history,
            new ChatOptions { ConversationId = id, MaxOutputTokens = 80 },
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(EdgeChatStopReason.Completed, second.GetTurnStatus()!.StopReason);
        Assert.Equal(2, harness.Session.Generators.Count);
        Assert.True(cached.Disposed);
        Assert.Equal(id, second.ConversationId);

        // Fresh: the whole (reduced) prompt was appended, not a delta.
        var status = second.GetTurnStatus()!;
        Assert.Equal(status.PromptTokens, status.PromptTokensAppended);
        Assert.Equal(2, status.MessagesDropped);
    }
}
