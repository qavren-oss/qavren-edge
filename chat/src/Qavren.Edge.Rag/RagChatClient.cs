using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Rag.Internal;

namespace Qavren.Edge.Rag;

/// <summary>Retrieve, assemble, stream, cite. Ordinary MEAI middleware.</summary>
/// <remarks>
/// <para>
/// It is <see cref="DelegatingChatClient"/> middleware and <b>not</b> a facade: it references no
/// ONNX package, no <c>Qavren.Edge.Chat.Onnx</c> and no <c>Qavren.Edge.VectorData</c>, which is what
/// lets <c>UseRag()</c> sit over an Azure OpenAI client, an <see cref="ExtractiveChatClient"/>, or
/// anything else that is an <see cref="IChatClient"/>.
/// </para>
/// <para>
/// It does <b>not</b> type-test its inner client and has no special case for
/// <see cref="ExtractiveChatClient"/>. The structured sources travel inward over
/// <see cref="RagCitations.SourcesPropertyKey"/>, a documented public key, which is what keeps the
/// no-LLM floor reproducible outside this package.
/// </para>
/// </remarks>
public sealed class RagChatClient : DelegatingChatClient
{
    private readonly ILogger _logger;

    /// <summary>Creates the middleware over an inner client and a retriever.</summary>
    /// <param name="innerClient">The client the turn is delegated to.</param>
    /// <param name="retriever">Where the sources come from.</param>
    /// <param name="options">Null takes every <see cref="RagOptions"/> default.</param>
    /// <param name="loggerFactory">Null logs nothing.</param>
    public RagChatClient(
        IChatClient innerClient,
        IEdgeRetriever retriever,
        RagOptions? options = null,
        ILoggerFactory? loggerFactory = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(retriever);

        Retriever = retriever;
        Options = options ?? new RagOptions();
        _logger = loggerFactory?.CreateLogger<RagChatClient>() ?? NullLogger<RagChatClient>.Instance;
    }

    /// <summary>The retriever this pipeline asks.</summary>
    public IEdgeRetriever Retriever { get; }

    /// <summary>The options this pipeline was built with.</summary>
    public RagOptions Options { get; }

    internal RagStatistics Statistics { get; } = new();

    /// <summary>
    /// <c>GetStreamingResponseAsync(...).ToChatResponseAsync(ct)</c>, plus exactly one fix-up:
    /// the citation list is moved from the streaming carrier - an empty <c>TextContent</c> on the
    /// final update - onto the aggregated assistant message's first <c>TextContent</c>, whose
    /// <c>Text</c> is the same concatenation the spans already index, and the emptied carrier is
    /// dropped. The offsets are <b>not</b> recomputed, because the string does not change. The
    /// fix-up exists because MEAI's aggregation may coalesce adjacent <c>TextContent</c>s, so the
    /// content holding the full text is the only one whose identity survives it.
    /// </summary>
    /// <param name="messages">The conversation.</param>
    /// <param name="options">The caller's options. Never mutated.</param>
    /// <param name="cancellationToken">Cancels the turn. Never converted into another exception.</param>
    /// <returns>The aggregated response.</returns>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken)
            .ConfigureAwait(false);

        MoveCitationsOntoTheAggregatedText(response);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var conversation = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        if (Options.ShouldRetrieve is { } shouldRetrieve && !shouldRetrieve(conversation, options))
        {
            await foreach (var passthrough in InnerClient
                .GetStreamingResponseAsync(conversation, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return passthrough;
            }

            yield break;
        }

        Statistics.Ask();

        var retrieval = await RetrieveAsync(conversation, cancellationToken).ConfigureAwait(false);

        // Step 5. An empty *result* short-circuits; an empty result because the retriever THREW
        // does not - that turn continues ungrounded, which is what ContinueOnRetrievalFailure
        // promises.
        if (retrieval.Included.Count == 0 && !retrieval.Failed && Options.ShortCircuitOnNoContext)
        {
            RagLog.NoContext(_logger, Retriever.Name);
            Statistics.LastGrounded = false;

            var answer = new ChatResponseUpdate(ChatRole.Assistant, RagPrompts.DefaultNoContextAnswer)
            {
                FinishReason = ChatFinishReason.Stop,
            };

            if (Options.AttachSourcesToResponse)
            {
                Publish(answer, [], grounded: false);
            }

            yield return answer;
            yield break;
        }

        var innerMessages = retrieval.Block is { Length: > 0 } block
            ? InjectContext(conversation, block)
            : conversation;

        // Step 6, second carrier. The caller's instance is never written to: MEAI's contract
        // permits an implementation to mutate what it is handed, which is exactly why this one
        // does not.
        var innerOptions = options?.Clone() ?? new ChatOptions();
        if (retrieval.Included.Count > 0)
        {
            innerOptions.AdditionalProperties ??= [];
            innerOptions.AdditionalProperties[RagCitations.SourcesPropertyKey] = retrieval.Included;
        }

        var accumulated = new StringBuilder();
        ChatResponseUpdate? pending = null;

        await foreach (var update in InnerClient
            .GetStreamingResponseAsync(innerMessages, innerOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            if (pending is not null)
            {
                yield return pending;
            }

            accumulated.Append(update.Text);
            pending = update;
        }

        if (pending is null)
        {
            yield break;
        }

        Finalise(pending, accumulated.ToString(), retrieval);
        yield return pending;
    }

    /// <summary>
    /// Returns <c>this</c>, <see cref="IEdgeRetriever"/> and <see cref="RagOptions"/>, then
    /// delegates inward - so <c>GetService&lt;ChatClientMetadata&gt;()</c> still reaches the leaf
    /// through the stack, which is what keeps Semantic Kernel and Agent Framework working over a
    /// RAG pipeline.
    /// </summary>
    /// <param name="serviceType">The service asked for.</param>
    /// <param name="serviceKey">A key, for a keyed service.</param>
    /// <returns>The service, or null.</returns>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null)
        {
            if (serviceType == typeof(RagChatClient))
            {
                return this;
            }

            // Exactly the declared type, not "anything the retriever happens to satisfy": a
            // retriever that also implements the asked-for service (a composite one implementing
            // IChatClient is the obvious case) must not be handed back in place of delegating
            // inward, or GetService<ChatClientMetadata>() stops reaching the leaf - which is the
            // one guarantee this override exists to protect.
            if (serviceType == typeof(IEdgeRetriever))
            {
                return Retriever;
            }

            if (serviceType == typeof(RagOptions))
            {
                return Options;
            }
        }

        return base.GetService(serviceType, serviceKey);
    }

    private async ValueTask<Retrieval> RetrieveAsync(
        IReadOnlyList<ChatMessage> conversation, CancellationToken cancellationToken)
    {
        var query = BuildQuery(conversation);
        var keywords = (Options.KeywordExtractor ?? RagOptions.ExtractKeywords)(query);

        IReadOnlyList<RagSource> retrieved = [];
        var failed = false;
        var start = Stopwatch.GetTimestamp();

        try
        {
            var request = new RetrievalRequest { Top = Options.Top, Keywords = keywords };
            retrieved = await Retriever.RetrieveAsync(query, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failed = true;
            var failures = Statistics.RetrievalFailed();
            RagLog.RetrievalFailed(_logger, Retriever.Name, failures, error);

            if (!Options.ContinueOnRetrievalFailure)
            {
                throw new EdgeRagException(
                    EdgeErrorCode.RagRetrievalFailed,
                    "The retriever threw and RagOptions.ContinueOnRetrievalFailure is false.",
                    retrieverName: Retriever.Name,
                    remediation:
                        "Leave RagOptions.ContinueOnRetrievalFailure true to answer ungrounded " +
                        "instead, or fix the retriever - the original failure is the inner exception.",
                    innerException: error);
            }
        }

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Statistics.LastRetrievalMs = elapsedMs;
        Statistics.LastRetrievedCount = retrieved.Count;
        Statistics.LastScoreKind = retrieved.Count > 0 ? retrieved[0].ScoreKind : null;

        RagLog.Retrieved(_logger, Retriever.Name, retrieved.Count, elapsedMs);
        RagLog.RetrievalQuery(_logger, query);

        if (retrieved.Count == 0)
        {
            Statistics.LastContextTokens = 0;
            return new Retrieval([], null, Grounded: false, failed);
        }

        var block = RagPrompts.Format(retrieved, Options, out var included);
        var contextTokens = (Options.TokenCounter ?? RagPrompts.EstimateTokens)(block);

        Statistics.LastContextTokens = contextTokens;
        RagLog.ContextAssembled(_logger, included.Count, retrieved.Count, contextTokens);
        RagLog.ContextBlock(_logger, block);

        return new Retrieval(included, block, included.Count > 0, failed);
    }

    private string BuildQuery(IReadOnlyList<ChatMessage> conversation)
    {
        if (Options.QuerySource == RetrievalQuerySource.AllUserMessages)
        {
            var builder = new StringBuilder();
            foreach (var message in conversation)
            {
                if (message.Role != ChatRole.User || message.Text.Length == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(message.Text);
            }

            return builder.ToString();
        }

        for (var i = conversation.Count - 1; i >= 0; i--)
        {
            if (conversation[i].Role == ChatRole.User)
            {
                return conversation[i].Text;
            }
        }

        return string.Empty;
    }

    // Step 6, first carrier. Immediately before the NEWEST user message, pinned, so the chat
    // package's reducer cannot evict the grounding the whole recipe exists to supply.
    private List<ChatMessage> InjectContext(IReadOnlyList<ChatMessage> conversation, string block)
    {
        var contextMessage = new ChatMessage(Options.ContextRole, block)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RagCitations.ContextMessagePropertyKey] = true,
            },
        };

        var injected = new List<ChatMessage>(conversation.Count + 1);
        injected.AddRange(conversation);

        var insertAt = injected.Count;
        for (var i = injected.Count - 1; i >= 0; i--)
        {
            if (injected[i].Role == ChatRole.User)
            {
                insertAt = i;
                break;
            }
        }

        injected.Insert(insertAt, contextMessage);
        return injected;
    }

    private void Finalise(ChatResponseUpdate final, string answer, Retrieval retrieval)
    {
        if (Options.EmitCitations && retrieval.Included.Count > 0)
        {
            var citations = RagCitations.Build(answer, retrieval.Included, out var unresolved);

            RagCitations.AttachTo(final, citations);
            Statistics.LastCitationsAttached = citations.Count;
            Statistics.LastCitationsUnresolved = unresolved;

            RagLog.CitationsAttached(_logger, citations.Count, retrieval.Included.Count);

            if (unresolved > 0)
            {
                RagLog.CitationUnresolved(_logger, unresolved);
            }
        }

        RagLog.Answer(_logger, answer);
        Statistics.LastGrounded = retrieval.Grounded;

        if (Options.AttachSourcesToResponse)
        {
            Publish(final, retrieval.Included, retrieval.Grounded);
        }
    }

    private static void Publish(ChatResponseUpdate update, IReadOnlyList<RagSource> sources, bool grounded)
    {
        update.AdditionalProperties ??= [];
        update.AdditionalProperties[RagCitations.SourcesPropertyKey] = sources;
        update.AdditionalProperties[RagCitations.GroundedPropertyKey] = grounded;
    }

    // Spec 11 step 8's non-streaming fix-up, and nothing else.
    private static void MoveCitationsOntoTheAggregatedText(ChatResponse response)
    {
        foreach (var message in response.Messages)
        {
            TextContent? carrier = null;
            TextContent? target = null;

            foreach (var content in message.Contents)
            {
                if (content is not TextContent text)
                {
                    continue;
                }

                if (carrier is null && text.Text.Length == 0 && text.Annotations is { Count: > 0 })
                {
                    carrier = text;
                    continue;
                }

                target ??= text;
            }

            if (carrier is null || target is null)
            {
                continue;
            }

            target.Annotations ??= [];
            foreach (var annotation in carrier.Annotations!)
            {
                target.Annotations.Add(annotation);
            }

            message.Contents.Remove(carrier);
        }
    }

    private readonly record struct Retrieval(
        IReadOnlyList<RagSource> Included, string? Block, bool Grounded, bool Failed);
}
