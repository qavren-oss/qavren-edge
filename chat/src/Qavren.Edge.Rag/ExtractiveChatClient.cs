using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Rag.Internal;

namespace Qavren.Edge.Rag;

/// <summary>
/// The no-LLM floor: answers verbatim from the highest-ranked sources with their citations
/// attached. Needs no model, no natives and no memory - so the product still answers on a device
/// that refused the budget or never downloaded a model, and the entire RAG path is tier-1 testable.
/// It is an <see cref="IChatClient"/>, so <c>UseRag()</c> over it is the same call, the same
/// pipeline and the same <c>CitationAnnotation</c>s.
/// </summary>
public sealed class ExtractiveChatClient : IChatClient
{
    private static readonly ChatClientMetadata ClientMetadata = new("qavren.edge.rag.extractive");

    private readonly ExtractiveChatOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the extractive floor.
    /// </summary>
    /// <param name="options">Null takes every <see cref="ExtractiveChatOptions"/> default.</param>
    /// <param name="loggerFactory">
    /// Null logs nothing. It is the only addition to spec 7's declared signature, and it is there
    /// because <c>EdgeRagEventIds.ExtractiveAnswer</c> (966) has no other raise site - an event id
    /// nothing can log is an event id nobody can look for.
    /// </param>
    /// <remarks>
    /// It reads its sources from
    /// <c>options.AdditionalProperties[RagCitations.SourcesPropertyKey]</c> - the request-side entry
    /// <see cref="RagChatClient"/> sets on a cloned <c>ChatOptions</c> before invoking its inner
    /// client. It does <b>not</b> parse the rendered context block, so a consumer who replaces
    /// <c>RagOptions.ContextFormatter</c> does not thereby break the floor, and
    /// <see cref="RagChatClient"/> does <b>not</b> special-case it: the channel is a documented
    /// public key.
    /// <para>
    /// With no such entry - this client used bare, outside a <c>UseRag()</c> pipeline - it answers
    /// <see cref="ExtractiveChatOptions.NoResultsAnswer"/> with the grounded flag clear rather than
    /// throwing. That is the honest answer: it has no model and was given no sources.
    /// </para>
    /// </remarks>
    public ExtractiveChatClient(ExtractiveChatOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? new ExtractiveChatOptions();
        _logger = loggerFactory?.CreateLogger<ExtractiveChatClient>() ?? NullLogger<ExtractiveChatClient>.Instance;
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var answer = Answer(options, out var grounded);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
        {
            FinishReason = ChatFinishReason.Stop,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RagCitations.GroundedPropertyKey] = grounded,
            },
        };

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var answer = Answer(options, out var grounded);

        yield return new ChatResponseUpdate(ChatRole.Assistant, answer)
        {
            FinishReason = ChatFinishReason.Stop,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RagCitations.GroundedPropertyKey] = grounded,
            },
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

        if (serviceType == typeof(ChatClientMetadata))
        {
            return ClientMetadata;
        }

        if (serviceType == typeof(ExtractiveChatOptions))
        {
            return _options;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>Nothing to release. Declared because <see cref="IChatClient"/> requires it.</summary>
    public void Dispose()
    {
        // No unmanaged state: the floor holds a options object and a logger.
    }

    internal RagStatistics Statistics { get; } = new();

    private string Answer(ChatOptions? options, out bool grounded)
    {
        var sources = ReadSources(options);

        if (sources.Count == 0)
        {
            grounded = false;
            RagLog.ExtractiveAnswer(_logger, 0);
            return _options.NoResultsAnswer;
        }

        var builder = new StringBuilder();
        var taken = Math.Min(_options.MaxSources, sources.Count);

        for (var i = 0; i < taken; i++)
        {
            var source = sources[i];
            var ordinal = source.Ordinal > 0 ? source.Ordinal : i + 1;

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(RagPrompts.Clamp(source.Text, _options.MaxCharsPerSource));
            builder.Append(" [").Append(ordinal.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        grounded = true;
        var answer = builder.ToString();

        Statistics.ExtractiveAnswered();
        Statistics.LastGrounded = true;
        Statistics.LastRetrievedCount = sources.Count;

        RagLog.ExtractiveAnswer(_logger, taken);
        RagLog.ExtractiveAnswerText(_logger, answer);

        return answer;
    }

    private static IReadOnlyList<RagSource> ReadSources(ChatOptions? options) =>
        options?.AdditionalProperties is { } properties &&
        properties.TryGetValue(RagCitations.SourcesPropertyKey, out var value) &&
        value is IReadOnlyList<RagSource> sources
            ? sources
            : [];
}
