using Microsoft.Extensions.AI;

namespace Qavren.Edge.Chat;

/// <summary>
/// Implements MEAI's <b>stable</b> <c>IChatReducer</c>. System messages are preserved, pinned
/// messages are preserved, then whole <i>groups</i> are evicted oldest-first until the token budget
/// or <see cref="ChatHistoryOptions.MaxTurns"/> binds, with
/// <see cref="ChatHistoryOptions.MinimumPreservedMessages"/> as the floor. Counts with the model's
/// own tokenizer, not an estimate. Never throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Whole turns" needs a definition once the tail is not turn-shaped, so here it is.</b> An
/// <b>eviction group</b> is a non-system, non-pinned message together with every message after it
/// up to but excluding the next <c>ChatRole.User</c> message. On an ordinary history that is
/// exactly a user/assistant pair, which is the property the phrase is reaching for: an assistant
/// message is never evicted without the user message it answered. On a history whose tail is
/// user-then-user - which is what the RAG recipe produces, because it injects a pinned context
/// message immediately before the real question - the pinned message is not in any group at all,
/// and the real user message opens the newest group. Nothing is left dangling and nothing needs a
/// special case.
/// </para>
/// <para>
/// <b>The floor is system messages + pinned messages + the newest
/// <see cref="ChatHistoryOptions.MinimumPreservedMessages"/> messages</b>, and the reducer stops
/// there even if the budget is still exceeded. What happens next is the turn's job: it fails with
/// <see cref="EdgeErrorCode.ChatPromptTooLong"/> (7102). That is the deliberate trade - an answer
/// that silently lost its sources is worse than a turn that says the context did not fit and names
/// <c>RagOptions.MaxContextTokens</c> as the knob.
/// </para>
/// <para>
/// Public, and applied internally. <c>ReducingChatClient</c>, <c>UseChatReducer</c>,
/// <c>MessageCountingChatReducer</c> and <c>SummarizingChatReducer</c> are all
/// <c>[Experimental]</c>, and sub-project 4 does not put an experimental attribute on a shipped
/// public surface - but <c>IChatReducer</c> itself is stable, so a consumer who accepts that
/// warning can hand this instance straight to <c>UseChatReducer</c>.
/// </para>
/// </remarks>
public sealed class EdgeChatTokenBudgetReducer : IChatReducer
{
    private readonly Func<string, int> _countTokens;
    private readonly ChatHistoryOptions _options;

    /// <summary>Creates the reducer.</summary>
    /// <param name="countTokens">
    /// The model's own tokenizer, injected so the reducer is a tier-1 unit test with no natives.
    /// </param>
    /// <param name="options">The history budget.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public EdgeChatTokenBudgetReducer(Func<string, int> countTokens, ChatHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(countTokens);
        ArgumentNullException.ThrowIfNull(options);

        _countTokens = countTokens;
        _options = options;
    }

    /// <summary>Reduces a history to fit the budget, preserving system and pinned messages.</summary>
    /// <param name="messages">The history, oldest first.</param>
    /// <param name="cancellationToken">Unused; the reduction is synchronous and allocates nothing native.</param>
    /// <returns>The retained messages, in their original order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var history = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        if (history.Count == 0)
        {
            return Task.FromResult<IEnumerable<ChatMessage>>(history);
        }

        var exempt = new bool[history.Count];
        for (var i = 0; i < history.Count; i++)
        {
            exempt[i] = IsSystem(history[i]) || IsPinned(history[i]);
        }

        // The floor's third term: the newest MinimumPreservedMessages entries, whatever they are.
        var protectedFrom = Math.Max(0, history.Count - Math.Max(0, _options.MinimumPreservedMessages));

        var groups = BuildGroups(history, exempt);
        var evicted = new bool[history.Count];
        var liveGroups = groups.Count;

        while (Exceeded(history, evicted, liveGroups))
        {
            var index = NextEvictableGroup(groups, evicted, protectedFrom);
            if (index < 0)
            {
                // The floor. The reducer stops here even though the budget is still exceeded; the
                // turn raises 7102 rather than evicting the grounding.
                break;
            }

            foreach (var position in groups[index])
            {
                evicted[position] = true;
            }

            liveGroups--;
        }

        var retained = new List<ChatMessage>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            if (!evicted[i])
            {
                retained.Add(history[i]);
            }
        }

        return Task.FromResult<IEnumerable<ChatMessage>>(retained);
    }

    private static List<List<int>> BuildGroups(IReadOnlyList<ChatMessage> history, bool[] exempt)
    {
        var groups = new List<List<int>>();
        List<int>? current = null;

        for (var i = 0; i < history.Count; i++)
        {
            if (exempt[i])
            {
                // A system or pinned message is in NO group, which is precisely what stops a pinned
                // RAG block from riding along with the turn in front of it.
                continue;
            }

            if (current is null || history[i].Role == ChatRole.User)
            {
                current = [];
                groups.Add(current);
            }

            current.Add(i);
        }

        return groups;
    }

    private static int NextEvictableGroup(List<List<int>> groups, bool[] evicted, int protectedFrom)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (evicted[group[0]])
            {
                continue;
            }

            foreach (var position in group)
            {
                if (position >= protectedFrom)
                {
                    // This group reaches into the preserved tail, and every later group is newer
                    // still, so the floor has been reached.
                    return -1;
                }
            }

            return i;
        }

        return -1;
    }

    private bool IsSystem(ChatMessage message) =>
        _options.PreserveSystemMessages && message.Role == ChatRole.System;

    private bool IsPinned(ChatMessage message)
    {
        if (message.AdditionalProperties is not { } properties)
        {
            return false;
        }

        foreach (var key in _options.PinnedMessageKeys)
        {
            if (properties.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }

    private bool Exceeded(IReadOnlyList<ChatMessage> history, bool[] evicted, int liveGroups)
    {
        if (liveGroups > _options.MaxTurns)
        {
            return true;
        }

        var tokens = 0;
        for (var i = 0; i < history.Count; i++)
        {
            if (!evicted[i])
            {
                tokens += _countTokens(history[i].Text ?? string.Empty);
            }
        }

        return tokens > _options.MaxHistoryTokens;
    }
}
