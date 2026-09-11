using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Qavren.Edge.Rag.Tests.Fakes;

/// <summary>
/// An <see cref="IChatClient"/> that records what the middleware handed it and streams a fixed set
/// of text deltas back. It is the leaf in every <see cref="RagChatClient"/> test.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly string[] _deltas;

    /// <summary>Creates the leaf.</summary>
    /// <param name="deltas">The text deltas to stream, one update each.</param>
    public FakeChatClient(params string[] deltas) => _deltas = deltas;

    /// <summary>How many times the middleware invoked this client.</summary>
    public int CallCount { get; private set; }

    /// <summary>The messages the middleware passed inward on the last call.</summary>
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    /// <summary>The options instance the middleware passed inward on the last call.</summary>
    public ChatOptions? LastOptions { get; private set; }

    /// <summary>The sources the middleware published on the request-side carrier.</summary>
    public IReadOnlyList<RagSource> LastSources =>
        LastOptions?.AdditionalProperties is { } properties &&
        properties.TryGetValue(RagCitations.SourcesPropertyKey, out var value) &&
        value is IReadOnlyList<RagSource> sources
            ? sources
            : [];

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastMessages = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        LastOptions = options;

        foreach (var delta in _deltas)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, delta);
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
        {
            FinishReason = ChatFinishReason.Stop,
        };

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata("fake-leaf")
            : serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }
}
