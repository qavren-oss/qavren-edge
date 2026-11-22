using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 10(f)'s reuse rule as the pure functions the client calls, with no client, no
/// session and no natives. <see cref="ConversationCacheUnitTests"/> drives the same rules through a
/// whole turn.
/// </summary>
public class ConversationReuseRuleTests
{
    [Fact]
    public void OnlyTheSameNonNullIdWithTheCacheOnIsACandidate()
    {
        using var entry = Entry("conversation-a");

        Assert.Same(entry, ChatTurnPipeline.CacheCandidate(entry, "conversation-a", cacheEnabled: true));
        Assert.Null(ChatTurnPipeline.CacheCandidate(entry, "conversation-b", cacheEnabled: true));
        Assert.Null(ChatTurnPipeline.CacheCandidate(entry, requestedId: null, cacheEnabled: true));
        Assert.Null(ChatTurnPipeline.CacheCandidate(entry, "conversation-a", cacheEnabled: false));
        Assert.Null(ChatTurnPipeline.CacheCandidate(previous: null, "conversation-a", cacheEnabled: true));

        // Ordinal: an id differing only in case is another conversation.
        Assert.Null(ChatTurnPipeline.CacheCandidate(entry, "Conversation-A", cacheEnabled: true));
    }

    [Fact]
    public void AGuidedTurnIsNeverReusableAndNeitherIsAnyTurnWithTheCacheOff()
    {
        var guidance = new ChatGuidance("json_schema", """{"type":"object"}""");

        Assert.True(ChatTurnPipeline.IsReusable(cacheEnabled: true, guidance: null));
        Assert.False(ChatTurnPipeline.IsReusable(cacheEnabled: true, guidance));
        Assert.False(ChatTurnPipeline.IsReusable(cacheEnabled: false, guidance: null));
        Assert.False(ChatTurnPipeline.IsReusable(cacheEnabled: false, guidance));
    }

    [Theory]
    [InlineData(10UL, 5, 5, 20, true)]
    [InlineData(10UL, 5, 6, 20, false)]
    [InlineData(0UL, 0, 1, 1, true)]
    [InlineData(4096UL, 0, 1, 4096, false)]
    public void ACachedGeneratorFitsWhenHeldPlusDeltaPlusTheWholeAnswerIsWithinItsMaxLength(
        ulong held, int delta, int maxOutput, int maxLength, bool fits) =>
        Assert.Equal(fits, ChatTurnPipeline.FitsCachedGenerator(held, delta, maxOutput, maxLength));

    [Theory]
    [InlineData(true, 28, 10, 4096, 4096)]
    [InlineData(false, 28, 10, 4096, 38)]
    [InlineData(false, 4090, 10, 4096, 4096)]
    public void AReusableGeneratorIsBuiltToTheWholeContextAndAOneShotToWhatTheTurnNeeds(
        bool reusable, int prompt, int maxOutput, int context, int expected) =>
        Assert.Equal(expected, ChatTurnPipeline.GeneratorMaxLength(reusable, prompt, maxOutput, context));

    [Theory]
    [InlineData(EdgeChatStopReason.Completed, true)]
    [InlineData(EdgeChatStopReason.StopSequence, true)]
    [InlineData(EdgeChatStopReason.MaxOutputTokens, true)]
    [InlineData(EdgeChatStopReason.Cancelled, false)]
    [InlineData(EdgeChatStopReason.MemoryPressure, false)]
    [InlineData(EdgeChatStopReason.Suspended, false)]
    [InlineData(EdgeChatStopReason.Thermal, false)]
    [InlineData(EdgeChatStopReason.Error, false)]
    public void OnlyACleanStopIsKept(EdgeChatStopReason stop, bool kept) =>
        Assert.Equal(kept, ChatTurnPipeline.ShouldKeep(Result(stop), cacheable: true, terminationRequested: false, disposed: false));

    [Fact]
    public void ACleanStopIsStillDroppedWhenAnythingElseSaysNo()
    {
        var clean = Result(EdgeChatStopReason.Completed);

        Assert.True(ChatTurnPipeline.ShouldKeep(clean, cacheable: true, terminationRequested: false, disposed: false));
        Assert.False(ChatTurnPipeline.ShouldKeep(clean, cacheable: false, terminationRequested: false, disposed: false));
        Assert.False(ChatTurnPipeline.ShouldKeep(clean, cacheable: true, terminationRequested: true, disposed: false));
        Assert.False(ChatTurnPipeline.ShouldKeep(clean, cacheable: true, terminationRequested: false, disposed: true));
        Assert.False(ChatTurnPipeline.ShouldKeep(result: null, cacheable: true, terminationRequested: false, disposed: false));
        Assert.False(ChatTurnPipeline.ShouldKeep(clean with { Cancelled = true }, cacheable: true, terminationRequested: false, disposed: false));
        Assert.False(ChatTurnPipeline.ShouldKeep(
            clean with { Failure = new InvalidOperationException("native fault") },
            cacheable: true,
            terminationRequested: false,
            disposed: false));
    }

    private static ConversationCache.Entry Entry(string conversationId)
    {
        var session = new FakeChatSession();
        var info = TestInfo.Build(ChatPresets.Llama32_1BInstructInt4);
        return new ConversationCache.Entry(
            conversationId,
            new ChatModelLease(null!, null!, null!, info, static () => { }),
            session,
            session.CreateGenerator([], guidance: null),
            cachedText: "<user>one</user>\n<assistant>",
            maxLength: 4096);
    }

    private static ChatDecodeResult Result(EdgeChatStopReason stop) =>
        new(
            new ChatTurnStatus(
                stop,
                "model",
                "conversation",
                PromptTokens: 28,
                PromptTokensAppended: 28,
                GeneratedTokens: 3,
                ContextTokens: 4096,
                MessagesDropped: 0,
                TimeToFirstToken: TimeSpan.Zero,
                Duration: TimeSpan.Zero,
                TokensPerSecond: 0,
                EdgeThermalState.Nominal,
                ThermalThrottled: false),
            DecodedText: "Hello world.",
            Cancelled: false,
            Failure: null);
}
