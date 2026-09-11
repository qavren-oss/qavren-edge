using Microsoft.Extensions.AI;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// History reduction, counted with an injected <c>Func&lt;string, int&gt;</c> so no tokenizer - and
/// therefore no native - is needed. The trap this file exists for: an <b>eviction group</b> is a
/// non-system, non-pinned message together with every message after it up to but excluding the next
/// <c>ChatRole.User</c> message, which on an ordinary history is a user/assistant pair and on the
/// RAG recipe's user-then-user tail is something else entirely.
/// </summary>
public class HistoryReducerTests
{
    private const string PinnedKey = "qavren.edge.rag.context";

    private static readonly Func<string, int> CountByLength = static text => text.Length;

    private static ChatMessage System(string text) => new(ChatRole.System, text);

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage Pinned(string text) => new(ChatRole.User, text)
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [PinnedKey] = text },
    };

    /// <summary>
    /// The reduction is synchronous by construction - no tokenizer, no I/O, no native - so the
    /// returned task is always already completed and every test below is a plain synchronous one.
    /// </summary>
    private static List<ChatMessage> Reduce(IEnumerable<ChatMessage> messages, ChatHistoryOptions options)
    {
        var reducer = new EdgeChatTokenBudgetReducer(CountByLength, options);
        var task = reducer.ReduceAsync(messages, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully, "The reducer allocates nothing that could make it asynchronous.");

        return [.. task.Result];
    }

    private static string[] Texts(IEnumerable<ChatMessage> messages) => [.. messages.Select(m => m.Text)];

    // ---- the default that the RAG recipe depends on ------------------------------------------------

    [Fact]
    public void ThePinnedKeyDefaultIsTheRagContextMessagePropertyKeyLiteral()
    {
        // Duplicated rather than referenced, because Qavren.Edge.Chat.Onnx must not depend on
        // Qavren.Edge.Rag. Wave 6 adds the cross-package assertion that the two strings are equal;
        // this is the half that can run before that project reference exists.
        var options = new ChatHistoryOptions();

        Assert.Equal([PinnedKey], options.PinnedMessageKeys);
    }

    [Fact]
    public void TheDefaultsAreEightTurnsOneThousandAndTwentyFourTokensAndAFloorOfTwo()
    {
        var options = new ChatHistoryOptions();

        Assert.Equal(8, options.MaxTurns);
        Assert.Equal(1024, options.MaxHistoryTokens);
        Assert.True(options.PreserveSystemMessages);
        Assert.Equal(2, options.MinimumPreservedMessages);
    }

    // ---- ordinary histories --------------------------------------------------------------------------

    [Fact]
    public void WholeGroupsAreEvictedOldestFirstAndTheSystemMessageSurvives()
    {
        ChatMessage[] history =
        [
            System("system0000"),
            User("user000001"), Assistant("asst000001"),
            User("user000002"), Assistant("asst000002"),
            User("user000003"), Assistant("asst000003"),
        ];

        var reduced = Reduce(history, new ChatHistoryOptions { MaxHistoryTokens = 25 });

        Assert.Equal(["system0000", "user000003", "asst000003"], Texts(reduced));
        Assert.Equal(4, history.Length - reduced.Count);
    }

    [Fact]
    public void AnAssistantMessageIsNeverEvictedWithoutTheUserMessageItAnswered()
    {
        ChatMessage[] history =
        [
            User("user000001"), Assistant("asst000001"),
            User("user000002"), Assistant("asst000002"),
            User("user000003"), Assistant("asst000003"),
        ];

        var reduced = Reduce(history, new ChatHistoryOptions { MaxHistoryTokens = 25 });

        // Whatever survives, every assistant message in it is preceded by a user message.
        for (var i = 0; i < reduced.Count; i++)
        {
            if (reduced[i].Role == ChatRole.Assistant)
            {
                Assert.True(i > 0 && reduced[i - 1].Role == ChatRole.User);
            }
        }
    }

    [Fact]
    public void MaxTurnsBindsEvenWhenTheTokenBudgetDoesNot()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            history.Add(User($"user{i:D6}"));
            history.Add(Assistant($"asst{i:D6}"));
        }

        var reduced = Reduce(history, new ChatHistoryOptions { MaxTurns = 2, MaxHistoryTokens = int.MaxValue });

        Assert.Equal(4, reduced.Count);
        Assert.Equal(["user000008", "asst000008", "user000009", "asst000009"], Texts(reduced));
    }

    [Fact]
    public void AHistoryInsideTheBudgetIsReturnedUntouched()
    {
        ChatMessage[] history = [System("s"), User("u"), Assistant("a")];

        var reduced = Reduce(history, new ChatHistoryOptions());

        Assert.Equal(Texts(history), Texts(reduced));
    }

    [Fact]
    public void AnEmptyHistoryIsReturnedEmpty()
        => Assert.Empty(Reduce([], new ChatHistoryOptions()));

    [Fact]
    public void PreserveSystemMessagesFalseMakesTheSystemMessageEvictableLikeAnyOther()
    {
        ChatMessage[] history =
        [
            System("system0000"),
            User("user000001"), Assistant("asst000001"),
            User("user000002"), Assistant("asst000002"),
        ];

        var reduced = Reduce(
            history,
            new ChatHistoryOptions { MaxHistoryTokens = 25, PreserveSystemMessages = false });

        Assert.DoesNotContain("system0000", Texts(reduced));
    }

    // ---- the floor -------------------------------------------------------------------------------------

    [Fact]
    public void TheReducerStopsAtTheFloorEvenThoughTheBudgetIsStillExceeded()
    {
        ChatMessage[] history =
        [
            System("system0000"),
            User("user000001"), Assistant("asst000001"),
            User("user000002"), Assistant("asst000002"),
        ];

        var reduced = Reduce(history, new ChatHistoryOptions { MaxHistoryTokens = 1 });

        // system + the newest MinimumPreservedMessages. What happens next is the turn's job: 7102,
        // never evicting the grounding.
        Assert.Equal(["system0000", "user000002", "asst000002"], Texts(reduced));
    }

    [Fact]
    public void RaisingMinimumPreservedMessagesRaisesTheFloor()
    {
        ChatMessage[] history =
        [
            User("user000001"), Assistant("asst000001"),
            User("user000002"), Assistant("asst000002"),
            User("user000003"), Assistant("asst000003"),
        ];

        var reduced = Reduce(
            history,
            new ChatHistoryOptions { MaxHistoryTokens = 1, MinimumPreservedMessages = 4 });

        Assert.Equal(4, reduced.Count);
    }

    // ---- the user-then-user tail the RAG recipe produces ------------------------------------------------

    [Fact]
    public void AUserThenUserTailReducesWithoutStrandingAnAssistantMessage()
    {
        ChatMessage[] history =
        [
            System("system0000"),
            User("user000001"), Assistant("asst000001"),
            User("user000002"), Assistant("asst000002"),
            Pinned("retrieved00"),
            User("question001"),
        ];

        var reduced = Reduce(history, new ChatHistoryOptions { MaxHistoryTokens = 25 });

        Assert.Equal(["system0000", "retrieved00", "question001"], Texts(reduced));
        Assert.DoesNotContain(reduced, m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public void ABudgetTooSmallToHoldThePinnedBlockLeavesItInPlace()
    {
        ChatMessage[] history =
        [
            User("user000001"), Assistant("asst000001"),
            Pinned("a very long retrieved context block that alone blows the budget"),
            User("question001"),
        ];

        var reduced = Reduce(history, new ChatHistoryOptions { MaxHistoryTokens = 1 });

        Assert.Contains(reduced, m => m.AdditionalProperties?.ContainsKey(PinnedKey) == true);
        Assert.Equal(2, reduced.Count);
    }

    [Fact]
    public void APinnedMessageIsNeverEvictedEvenAsTheOldestMessageInTheHistory()
    {
        ChatMessage[] history =
        [
            Pinned("pinned0000"),
            User("user000001"), Assistant("asst000001"),
            User("user000002"), Assistant("asst000002"),
            User("user000003"), Assistant("asst000003"),
        ];

        var reduced = Reduce(history, new ChatHistoryOptions { MaxHistoryTokens = 1 });

        Assert.Equal(["pinned0000", "user000003", "asst000003"], Texts(reduced));
    }

    [Fact]
    public void AnAdditionalPropertyThatIsNotAPinnedKeyDoesNotPin()
    {
        var decorated = User("user000001");
        decorated.AdditionalProperties = new AdditionalPropertiesDictionary { ["something.else"] = 1 };

        ChatMessage[] history =
        [
            decorated, Assistant("asst000001"),
            User("user000002"), Assistant("asst000002"),
            User("user000003"), Assistant("asst000003"),
        ];

        var reduced = Reduce(history, new ChatHistoryOptions { MaxHistoryTokens = 25 });

        Assert.DoesNotContain("user000001", Texts(reduced));
    }

    [Fact]
    public void ClearingPinnedMessageKeysMakesTheRagBlockEvictableWhichIsWhyTheDefaultIsNotEmpty()
    {
        ChatMessage[] history =
        [
            User("user000001"), Assistant("asst000001"),
            Pinned("retrieved00"),
            User("question001"),
        ];

        var cleared = new ChatHistoryOptions { MaxHistoryTokens = 1, MinimumPreservedMessages = 1 };
        cleared.PinnedMessageKeys.Clear();

        var withoutPinning = Reduce(history, cleared);
        var withPinning = Reduce(
            history,
            new ChatHistoryOptions { MaxHistoryTokens = 1, MinimumPreservedMessages = 1 });

        // Same history, same budget, same floor: the only difference is the pinned key, and the
        // grounding is what it saves.
        Assert.DoesNotContain("retrieved00", Texts(withoutPinning));
        Assert.Contains("retrieved00", Texts(withPinning));
    }

    [Fact]
    public void TheReducerNeverReordersWhatItKeeps()
    {
        ChatMessage[] history =
        [
            System("system0000"),
            User("user000001"), Assistant("asst000001"),
            Pinned("retrieved00"),
            User("question001"),
        ];

        var reduced = Reduce(history, new ChatHistoryOptions { MaxHistoryTokens = 1 });
        var kept = Texts(reduced);
        var original = Texts(history).Where(t => kept.Contains(t, StringComparer.Ordinal)).ToArray();

        Assert.Equal(original, kept);
    }
}
