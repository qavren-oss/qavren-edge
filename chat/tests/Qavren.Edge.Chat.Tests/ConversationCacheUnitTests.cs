using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 10(f)'s conversation cache, the rules that need no natives: who mints the id, what
/// is never a hit, and the one string test that carries three invariants.
/// </summary>
public class ConversationCacheUnitTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static Task<ChatResponse> TurnAsync(ClientHarness harness, ChatMessage[] messages, string? conversationId) =>
        harness.Client.GetResponseAsync(messages, new ChatOptions { ConversationId = conversationId }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ANullIdMintsOneAndStampsItOnEveryUpdateAndOnTheResponse()
    {
        using var harness = ClientHarness.Build();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in harness.Client.GetStreamingResponseAsync([User("one")], null, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            updates.Add(update);
        }

        var id = Assert.Single(updates.Select(u => u.ConversationId).Distinct(StringComparer.Ordinal));
        Assert.NotNull(id);
        Assert.True(Guid.TryParseExact(id, "N", out _));

        var response = updates.ToChatResponse();
        Assert.Equal(id, response.ConversationId);
        Assert.Equal(id, response.GetTurnStatus()!.ConversationId);
        Assert.True(harness.Client.HasCachedConversation);
        Assert.True(harness.Host.HasConversationCache);
    }

    [Fact]
    public async Task EchoingTheIdHitsAndOnlyTheDeltaIsAppended()
    {
        using var harness = ClientHarness.Build(script: ["reply"]);

        var first = await TurnAsync(harness, [User("one")], conversationId: null).ConfigureAwait(true);
        var id = first.ConversationId!;
        var firstStatus = first.GetTurnStatus()!;
        Assert.Equal(firstStatus.PromptTokens, firstStatus.PromptTokensAppended);

        var second = await TurnAsync(harness, [User("one"), Assistant(first.Text), User("two")], id).ConfigureAwait(true);

        var generator = Assert.Single(harness.Session.Generators);
        Assert.Equal(2, generator.Appended.Count);

        var status = second.GetTurnStatus()!;
        Assert.Equal(id, second.ConversationId);

        // Total conditioned on, versus what this turn encoded: both are published, because "fewer
        // prompt tokens on the second turn" is true of one and false of the other.
        Assert.True(status.PromptTokensAppended < status.PromptTokens);
        Assert.Equal((int)generator.TokenCount(), status.PromptTokens);
        Assert.Equal(generator.Appended[1].Length, status.PromptTokensAppended);
        Assert.Equal(1, harness.Host.Acquires);
        Assert.Equal(1, harness.Host.ActiveLeases);
    }

    [Fact]
    public async Task ADifferentIdRebuilds()
    {
        using var harness = ClientHarness.Build();

        var first = await TurnAsync(harness, [User("one")], "conversation-a").ConfigureAwait(true);
        Assert.Equal("conversation-a", first.ConversationId);

        var second = await TurnAsync(harness, [User("one")], "conversation-b").ConfigureAwait(true);

        Assert.Equal("conversation-b", second.ConversationId);
        Assert.Equal(2, harness.Session.Generators.Count);
        Assert.True(harness.Session.Generators[0].Disposed);
        Assert.False(harness.Session.Generators[1].Disposed);
        Assert.Equal(1, harness.Host.ActiveLeases);
    }

    [Fact]
    public async Task ANullIdOnTurnTwoRebuildsRatherThanReusingTurnOnesCache()
    {
        // Two unrelated conversations that both left the field null must never share a KV cache.
        using var harness = ClientHarness.Build();

        var first = await TurnAsync(harness, [User("one")], conversationId: null).ConfigureAwait(true);
        var second = await TurnAsync(harness, [User("one")], conversationId: null).ConfigureAwait(true);

        Assert.NotEqual(first.ConversationId, second.ConversationId);
        Assert.Equal(2, harness.Session.Generators.Count);
        Assert.True(harness.Session.Generators[0].Disposed);
    }

    [Fact]
    public async Task AMutatedEarlierMessageFailsThePrefixCheckAndRebuilds()
    {
        using var harness = ClientHarness.Build(script: ["reply"]);

        var first = await TurnAsync(harness, [User("one")], conversationId: null).ConfigureAwait(true);
        var id = first.ConversationId!;

        await TurnAsync(harness, [User("ONE, edited"), Assistant(first.Text), User("two")], id).ConfigureAwait(true);

        Assert.Equal(2, harness.Session.Generators.Count);
        Assert.True(harness.Session.Generators[0].Disposed);
        Assert.Single(harness.Session.Generators[1].Appended);
    }

    [Fact]
    public async Task WithCachingOffNoIdIsMintedAndTheGeneratorAndLeaseGoAtTheEndOfTheTurn()
    {
        using var harness = ClientHarness.Build(o => o.EnableConversationCache = false);

        var response = await TurnAsync(harness, [User("one")], conversationId: null).ConfigureAwait(true);

        Assert.Null(response.ConversationId);
        Assert.Null(response.GetTurnStatus()!.ConversationId);
        Assert.True(harness.Session.LastGenerator.Disposed);
        Assert.Equal(0, harness.Host.ActiveLeases);
        Assert.False(harness.Client.HasCachedConversation);

        // A caller's own id is echoed unchanged.
        response = await TurnAsync(harness, [User("one")], "mine").ConfigureAwait(true);
        Assert.Equal("mine", response.ConversationId);
        Assert.Equal(2, harness.Session.Generators.Count);
    }

    [Fact]
    public async Task WithCachingOffMaxLengthIsWhatTheTurnNeedsAndWithItOnItIsTheBudgetsAnswer()
    {
        using var off = ClientHarness.Build(o => { o.EnableConversationCache = false; o.MaxOutputTokens = 10; }, resolvedContext: 4096);
        await TurnAsync(off, [User("one")], null).ConfigureAwait(true);
        Assert.Equal(28 + 10, FakeChatSession.Number(off.Session.SearchOptions[0], "max_length"));

        using var on = ClientHarness.Build(o => o.MaxOutputTokens = 10, resolvedContext: 4096);
        await TurnAsync(on, [User("one")], null).ConfigureAwait(true);
        Assert.Equal(4096, FakeChatSession.Number(on.Session.SearchOptions[0], "max_length"));
    }

    [Fact]
    public async Task DroppingTheCacheThroughTheHostDisposesTheGeneratorAndReturnsTheLease()
    {
        using var harness = ClientHarness.Build();

        await TurnAsync(harness, [User("one")], conversationId: null).ConfigureAwait(true);
        Assert.Equal(1, harness.Host.ActiveLeases);

        harness.Host.DropConversationCache();

        Assert.True(harness.Session.LastGenerator.Disposed);
        Assert.Equal(0, harness.Host.ActiveLeases);
        Assert.False(harness.Client.HasCachedConversation);
    }

    [Fact]
    public async Task ADropThatArrivesMidTurnIsHonouredAtTheEndOfTheTurnNotUnderTheRunningGenerator()
    {
        using var harness = ClientHarness.Build(script: ["a", "b", "c"]);
        harness.Session.OnGeneratorCreated = generator => generator.BeforeGenerate = (g, index) =>
        {
            if (index == 1)
            {
                harness.Host.DropConversationCache();
                Assert.False(g.Disposed);
            }
        };

        var response = await TurnAsync(harness, [User("one")], conversationId: null).ConfigureAwait(true);

        Assert.Equal("abc", response.Text);
        Assert.True(harness.Session.LastGenerator.Disposed);
        Assert.False(harness.Client.HasCachedConversation);
        Assert.Equal(0, harness.Host.ActiveLeases);
    }

    [Fact]
    public async Task DisposingTheClientDropsTheCacheAndUnregistersFromTheHost()
    {
        var harness = ClientHarness.Build();
        await TurnAsync(harness, [User("one")], conversationId: null).ConfigureAwait(true);

        harness.Client.Dispose();

        Assert.True(harness.Session.LastGenerator.Disposed);
        Assert.Equal(0, harness.Host.ActiveLeases);
        Assert.False(harness.Host.HasConversationCache);
        Assert.Throws<ObjectDisposedException>(() => harness.Client.GetStreamingResponseAsync([User("x")], null, TestContext.Current.CancellationToken));
    }
}
