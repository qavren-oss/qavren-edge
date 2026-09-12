using Microsoft.Extensions.AI;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// Spec 10(f)'s conversation cache, all five rules against real natives, plus spec 19 item 11's
/// seam check run here rather than deferred.
/// </summary>
/// <remarks>
/// The first message is deliberately longer than the follow-up: the fixture's tokenizer is
/// byte-level (one token per byte, measured), and "PromptTokensAppended on turn two strictly less
/// than turn one's" has to be true of the bytes the template adds, not of a lucky choice of words.
/// </remarks>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class ConversationCacheTests(TinyChatModelFixture fixture)
{
    private const string FirstQuestion = "tell me about the sealed compressor cover";
    private const string FollowUp = "and labour?";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static Task<ChatResponse> TurnAsync(TierTwoClient client, ChatMessage[] messages, string? conversationId) =>
        client.Client.GetResponseAsync(messages, new ChatOptions { ConversationId = conversationId }, Token);

    [Fact]
    public async Task AFirstTurnWithANullIdMintsOneAndStampsItOnEveryUpdateAndOnTheResponse()
    {
        using var client = fixture.NewClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.Client.GetStreamingResponseAsync([User(FirstQuestion)], null, Token).ConfigureAwait(true))
        {
            updates.Add(update);
        }

        var id = Assert.Single(updates.Select(u => u.ConversationId).Distinct(StringComparer.Ordinal));
        Assert.NotNull(id);
        Assert.True(Guid.TryParseExact(id, "N", out _));

        var response = updates.ToChatResponse();
        Assert.Equal(id, response.ConversationId);
        Assert.Equal(id, response.GetTurnStatus()!.ConversationId);

        // The generator - and the lease under it - are held for the follow-up.
        Assert.True(client.Client.HasCachedConversation);
        Assert.Equal(1, client.Host.Host.Describe()!.ActiveLeases);
    }

    [Fact]
    public async Task EchoingTheIdHitsWithFewerTokensAppendedAndMoreTokensConditionedOn()
    {
        using var client = fixture.NewClient();

        var first = await TurnAsync(client, [User(FirstQuestion)], conversationId: null).ConfigureAwait(true);
        var id = first.ConversationId!;
        var firstStatus = first.GetTurnStatus()!;
        Assert.Equal(firstStatus.PromptTokens, firstStatus.PromptTokensAppended);

        var second = await TurnAsync(client, [User(FirstQuestion), Assistant(first.Text), User(FollowUp)], id).ConfigureAwait(true);
        var status = second.GetTurnStatus()!;

        // A hit: one generator for two turns, one lease, one session.
        Assert.Equal(1, client.Sessions.GeneratorsBuilt);
        Assert.Equal(1, client.Sessions.Sessions);
        Assert.Equal(id, second.ConversationId);

        // Both numbers are published because "fewer prompt tokens on the second turn" is true of
        // one and false of the other.
        Assert.True(status.PromptTokensAppended < status.PromptTokens);
        Assert.True(status.PromptTokensAppended < firstStatus.PromptTokensAppended);
        Assert.True(status.PromptTokens > firstStatus.PromptTokens);

        // The total the model conditioned on: the first prompt, its reply, and the delta.
        Assert.Equal(
            firstStatus.PromptTokens + firstStatus.GeneratedTokens + status.PromptTokensAppended,
            status.PromptTokens);

        Assert.Equal(1, client.Host.Host.Describe()!.ActiveLeases);
    }

    [Fact]
    public async Task ADifferentIdRebuilds()
    {
        using var client = fixture.NewClient();

        var first = await TurnAsync(client, [User(FirstQuestion)], "conversation-a").ConfigureAwait(true);
        Assert.Equal("conversation-a", first.ConversationId);

        var second = await TurnAsync(client, [User(FirstQuestion)], "conversation-b").ConfigureAwait(true);

        Assert.Equal("conversation-b", second.ConversationId);
        Assert.Equal(2, client.Sessions.GeneratorsBuilt);
        Assert.Equal(second.GetTurnStatus()!.PromptTokens, second.GetTurnStatus()!.PromptTokensAppended);

        // The evicted generator returned its lease; the new one holds exactly one.
        Assert.Equal(1, client.Host.Host.Describe()!.ActiveLeases);
    }

    [Fact]
    public async Task ANullIdOnTurnTwoRebuildsRatherThanReusingTurnOnesCache()
    {
        // Two unrelated conversations that both left the field null must never share a KV cache.
        using var client = fixture.NewClient();

        var first = await TurnAsync(client, [User(FirstQuestion)], conversationId: null).ConfigureAwait(true);
        var second = await TurnAsync(client, [User(FirstQuestion)], conversationId: null).ConfigureAwait(true);

        Assert.NotNull(first.ConversationId);
        Assert.NotNull(second.ConversationId);
        Assert.NotEqual(first.ConversationId, second.ConversationId);
        Assert.Equal(2, client.Sessions.GeneratorsBuilt);
        Assert.Equal(1, client.Host.Host.Describe()!.ActiveLeases);
    }

    [Fact]
    public async Task AMutatedHistoryFailsThePrefixCheckAndRebuildsRatherThanDuplicatingTheConversation()
    {
        using var client = fixture.NewClient();

        var first = await TurnAsync(client, [User(FirstQuestion)], conversationId: null).ConfigureAwait(true);
        var id = first.ConversationId!;

        var second = await TurnAsync(
            client,
            [User(FirstQuestion.ToUpperInvariant()), Assistant(first.Text), User(FollowUp)],
            id).ConfigureAwait(true);

        Assert.Equal(2, client.Sessions.GeneratorsBuilt);

        // Rebuilt from scratch: everything was appended, nothing was reused.
        var status = second.GetTurnStatus()!;
        Assert.Equal(status.PromptTokens, status.PromptTokensAppended);
        Assert.Equal(id, second.ConversationId);
    }

    [Fact]
    public async Task TheAppendPathEncodesTheSameTokensAsEncodingTheWholeRenderedText()
    {
        // Spec 19 item 11: the cache appends only fullText[cachedText.Length..], encoded on its
        // own, so a BPE merge that would have spanned the join would be lost. The check is exact:
        // encode the three pieces the append path sees - the first prompt, the reply, the delta -
        // and compare against encoding the whole second prompt at once.
        using var client = fixture.NewClient();

        var first = await TurnAsync(client, [User(FirstQuestion)], conversationId: null).ConfigureAwait(true);
        await TurnAsync(client, [User(FirstQuestion), Assistant(first.Text), User(FollowUp)], first.ConversationId).ConfigureAwait(true);

        Assert.Equal(1, client.Sessions.GeneratorsBuilt);

        var rendered = client.Sessions.TemplateOutputs;
        Assert.Equal(2, rendered.Count);

        var firstPrompt = rendered[0];
        var reply = first.Text;
        var secondPrompt = rendered[1];
        var cachedText = firstPrompt + reply;

        Assert.StartsWith(cachedText, secondPrompt, StringComparison.Ordinal);
        var delta = secondPrompt[cachedText.Length..];

        int[] piecewise = [.. client.Sessions.Encode(firstPrompt), .. client.Sessions.Encode(reply), .. client.Sessions.Encode(delta)];
        var whole = client.Sessions.Encode(secondPrompt);

        JobSummary.Record("tier2-cache-seam", "firstPromptTokens", client.Sessions.Encode(firstPrompt).Length);
        JobSummary.Record("tier2-cache-seam", "deltaTokens", client.Sessions.Encode(delta).Length);
        JobSummary.Record("tier2-cache-seam", "wholeTokens", whole.Length);
        JobSummary.Record("tier2-cache-seam", "replyContainsReplacementChar", reply.Contains('�', StringComparison.Ordinal));
        JobSummary.Record("tier2-cache-seam", "seamLossless", piecewise.AsSpan().SequenceEqual(whole));

        Assert.Equal(whole, piecewise);
    }

    [Fact]
    public async Task WithCachingOffNoIdIsMintedAndTheLeaseGoesBackAtTheEndOfTheTurn()
    {
        using var client = fixture.NewClient(o => o.EnableConversationCache = false);

        var response = await TurnAsync(client, [User(FirstQuestion)], conversationId: null).ConfigureAwait(true);

        Assert.Null(response.ConversationId);
        Assert.Null(response.GetTurnStatus()!.ConversationId);
        Assert.False(client.Client.HasCachedConversation);
        Assert.Equal(0, client.Host.Host.Describe()!.ActiveLeases);
        Assert.True(client.Host.Host.Describe()!.IsLoaded);
    }
}
