using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Onnx;
using GenAiModel = Microsoft.ML.OnnxRuntimeGenAI.Model;

namespace Qavren.Edge.Chat;

/// <summary>MEAI <see cref="IChatClient"/> over ORT GenAI.</summary>
/// <remarks>
/// <para>
/// <b>Thread safety.</b> <c>src/ort_genai_c.h</c> states flatly "This API is not thread safe",
/// while <see cref="IChatClient"/>'s own contract requires every member to be safe for concurrent
/// use. Both are honoured by SERIALISATION, not parallelism: one async gate per model holds for a
/// whole turn, and a second concurrent caller queues rather than allocating a second
/// <c>Generator</c> and therefore a second full KV cache. On a phone that is the point; on a server
/// it is a throughput ceiling, and the README says so rather than letting someone read it as a bug.
/// </para>
/// <para>
/// <b>One implementation, not two.</b> <see cref="GetResponseAsync"/> is literally
/// <see cref="GetStreamingResponseAsync"/> aggregated, because two independent implementations is
/// how the streaming and non-streaming paths drift. Production runs on one <c>Task.Run</c> writing
/// a bounded channel of 64 updates; the iterator reads it, so the caller's UI thread never enters
/// native code and backpressure is real.
/// </para>
/// <para>
/// <b>Nothing native is touched until the first turn.</b> Resolving this client constructs no
/// GenAI object; the model is borrowed from <see cref="IChatModelHost"/> per turn, or held for as
/// long as the conversation cache keeps a generator alive.
/// </para>
/// </remarks>
public sealed class EdgeChatClient : IChatClient
{
    private readonly ChatRegistration _registration;
    private readonly IChatTurnHost _host;
    private readonly IEdgeResourceMonitor _monitor;
    private readonly ChatStatistics _statistics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ChatSessionFactory _sessionFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConversationCache _cache = new();
    private readonly ChatClientMetadata _metadata;
    private int _queued;
    private int _gateEntries;
    private volatile bool _disposed;

    /// <summary>Creates the client for one registration. Only <c>AddOnnxChat</c> and the tests call this.</summary>
    /// <param name="registration">The preset and options.</param>
    /// <param name="host">The model host.</param>
    /// <param name="monitor">The device resource monitor, read before and during every turn.</param>
    /// <param name="statistics">The counters this client writes and diagnostics read.</param>
    /// <param name="timeProvider">The clock the thermal sampling window and the pacing use.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="sessionFactory">The native seam; null takes the real ORT GenAI one.</param>
    internal EdgeChatClient(
        ChatRegistration registration,
        IChatTurnHost host,
        IEdgeResourceMonitor monitor,
        ChatStatistics statistics,
        TimeProvider timeProvider,
        ILogger logger,
        ChatSessionFactory? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _registration = registration;
        _host = host;
        _monitor = monitor;
        _statistics = statistics;
        _timeProvider = timeProvider;
        _logger = logger;
        _sessionFactory = sessionFactory ?? GenAiChatSession.Create;
        _metadata = new ChatClientMetadata(ChatTurnPipeline.ProviderName, defaultModelId: ModelId);

        _host.SetConversationCache(_cache.Drop);
    }

    /// <summary>The preset manifest's model id, which is also <c>ChatClientMetadata.DefaultModelId</c>.</summary>
    public string ModelId => _registration.Preset.Manifest.ModelId;

    /// <summary>What this client has done since it was resolved.</summary>
    public ChatClientStatistics Statistics => _statistics.Snapshot(UnloadEvents());

    /// <summary>What the host last knew about the model, or null if it has never loaded one.</summary>
    public ChatModelInfo? Model => _host.Describe();

    /// <summary>Turns currently waiting for the gate.</summary>
    internal int QueuedTurns => Volatile.Read(ref _queued);

    /// <summary>How many turns have entered the gate. The thermal pre-flight tests assert zero.</summary>
    internal int GateEntries => Volatile.Read(ref _gateEntries);

    /// <summary>Whether the conversation cache holds a generator right now.</summary>
    internal bool HasCachedConversation => _cache.HasEntry;

    /// <summary>
    /// Implemented as <c>GetStreamingResponseAsync(...).ToChatResponseAsync(ct)</c>, so the two
    /// paths cannot diverge, plus one hoist: MEAI's aggregation lands an update's
    /// <c>AdditionalProperties</c> on the aggregated assistant message, and spec section 6.6 puts
    /// the turn record on the <see cref="ChatResponse"/>, so the two documented keys are copied up.
    /// Nothing else differs between the two paths.
    /// </summary>
    /// <param name="messages">The conversation.</param>
    /// <param name="options">The per-call options. Never mutated.</param>
    /// <param name="cancellationToken">Cancels the turn. Never converted into another exception.</param>
    /// <returns>The aggregated response.</returns>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken)
            .ConfigureAwait(false);

        HoistTurnMetadata(response);
        return response;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var conversation = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        return StreamAsync(conversation, options, cancellationToken);
    }

    /// <summary>
    /// Resolves, in order: <c>this</c> when <c>serviceKey is null &amp;&amp;
    /// serviceType.IsInstanceOfType(this)</c>; <see cref="ChatClientMetadata"/> with
    /// <c>ProviderName</c> <c>"onnxruntime-genai"</c> and <c>DefaultModelId</c> =
    /// <see cref="ModelId"/>; <see cref="ChatModelInfo"/>; <see cref="ChatClientStatistics"/>;
    /// <see cref="IChatModelHost"/>; and, while this client holds the model, <c>Model</c>,
    /// <c>Tokenizer</c> and <c>Config</c>.
    /// <para>
    /// <b>This method is the entire integration surface for three downstream consumers</b> and is
    /// a contract, not an implementation detail: Semantic Kernel's <c>GetModelId()</c> is literally
    /// <c>GetService&lt;ChatClientMetadata&gt;()?.DefaultModelId</c>, Agent Framework's
    /// <c>ChatClientAgent</c> avoids double-wrapping by calling
    /// <c>GetService&lt;FunctionInvokingChatClient&gt;()</c>, and MEAI's own
    /// <c>GetRequiredService&lt;T&gt;()</c> goes through it. Returning the raw <c>Model</c> is also
    /// what lets a consumer build <c>OnnxRuntimeGenAIChatClient</c>, <c>MultiModalProcessor</c> or
    /// LoRA <c>Adapters</c> themselves without sub-project 4 wrapping any of it.
    /// </para>
    /// <para>
    /// The three native objects are returned only while this client holds a lease - which, with
    /// the conversation cache on, is from the first turn until the cache is dropped. A model the
    /// host may dispose under the caller is never handed out.
    /// </para>
    /// </summary>
    /// <param name="serviceType">The requested type.</param>
    /// <param name="serviceKey">Must be null; a keyed request resolves nothing here.</param>
    /// <returns>The service, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serviceType"/> is null.</exception>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        if (serviceType == typeof(ChatModelInfo))
        {
            return Model;
        }

        if (serviceType == typeof(ChatClientStatistics))
        {
            return Statistics;
        }

        if (serviceType == typeof(IChatModelHost))
        {
            return _host;
        }

        if (_cache.PeekLease() is { } lease)
        {
            if (serviceType == typeof(GenAiModel))
            {
                return lease.Model;
            }

            if (serviceType == typeof(Tokenizer))
            {
                return lease.Tokenizer;
            }

            if (serviceType == typeof(Config))
            {
                return lease.Config;
            }
        }

        return null;
    }

    /// <summary>
    /// Releases this client's lease and conversation cache. Does not dispose the shared model -
    /// the host owns it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.SetConversationCache(null);
        _cache.Dispose();
        _gate.Dispose();
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var edge = _registration.Options;
        var startedAt = Stopwatch.GetTimestamp();

        // (a) Refuse early, by name - and validate the escape hatch before anything is borrowed.
        var unhonoured = new List<string>(ChatTurnPipeline.Refuse(messages, options, edge));
        ChatTurnPipeline.ComposeSearchOptions(ChatTurnPipeline.ResolveKnobs(options, edge), 0, options, edge);

        // (b) Gate: the pre-flight half, before the gate is entered and before anything is allocated.
        var snapshot = _monitor.Read();
        try
        {
            ChatTurnPipeline.PreFlight(_host, snapshot, edge);
        }
        catch (EdgeChatException ex)
        {
            _statistics.TurnRejected();
            ChatTurnLog.TurnRejected(_logger, ex.Message);
            throw;
        }

        // (b) Gate: the queue half.
        await EnterGateAsync(cancellationToken).ConfigureAwait(false);

        Turn turn;
        try
        {
            turn = await PrepareTurnAsync(messages, options, unhonoured, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseGate();
            throw;
        }

        var channel = Channel.CreateBounded<ChatResponseUpdate>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        using var consumerGone = new CancellationTokenSource();

        // Start clean BEFORE anything can terminate this turn: a cached generator whose previous
        // turn's terminate lost a race with FinishTurn would otherwise report done at once.
        ChatTurnPipeline.Resume(turn.Generator);
        using var termination = cancellationToken.Register(static state => TryTerminate((IChatGenerator)state!), turn.Generator);
        _host.SetActiveGeneration(turn.Generator.SetRuntimeOption);

        var context = new ChatDecodeContext
        {
            Generator = turn.Generator,
            Stream = turn.Stream,
            StopMatcher = new StopSequenceMatcher(
                EdgeChatOptions.ComposeStopSequences(_registration.Preset, edge, options?.StopSequences)),
            Writer = channel.Writer,
            Host = _host,
            Monitor = _monitor,
            TimeProvider = _timeProvider,
            Logger = _logger,
            Thermal = edge.Thermal,
            MaxOutputTokens = turn.MaxOutputTokens,
            MaxLength = turn.MaxLength,
            ResolvedContextTokens = turn.ResolvedContextTokens,
            PromptTokens = turn.PromptTokens,
            PromptTokensAppended = turn.PromptTokensAppended,
            MessagesDropped = turn.MessagesDropped,
            ModelId = ModelId,
            ConversationId = turn.ConversationId,
            ResponseId = Guid.NewGuid().ToString("N"),
            MessageId = Guid.NewGuid().ToString("N"),
            UnhonouredOptions = unhonoured,
            InitialSnapshot = snapshot,
            TurnStartedAt = startedAt,
            CancellationToken = cancellationToken,
            ConsumerGone = consumerGone.Token,
        };

        ChatTurnLog.TurnStarted(
            _logger,
            context.ResponseId,
            ModelId,
            turn.PromptTokens,
            turn.PromptTokensAppended,
            turn.GeneratorOutcome);

        // ---- producer: runs on Task.Run, writes the channel, never yields ----
        var producer = Task.Run(() => ChatDecodeLoop.RunAsync(context), CancellationToken.None);

        ChatDecodeResult? result = null;
        try
        {
            // ---- consumer: the iterator the caller awaits. It reads with no token of its own, so
            // a cancelled turn still delivers its final update before the exception surfaces. ----
            await foreach (var update in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            // Observe the producer, whatever happened: a cancelled or faulted turn is never an
            // unobserved exception. A consumer that stopped reading unblocks it first.
            consumerGone.Cancel();
            try
            {
                result = await producer.ConfigureAwait(false);
            }
            finally
            {
                // Unregister BEFORE the generator can be handed to the conversation cache. Dispose
                // waits for an in-flight callback, so after this line no TryTerminate can land on
                // a generator a LATER turn will reuse - which would leave terminate_session stuck
                // at "1" on a cached generator.
                termination.Dispose();
                FinishTurn(turn, result);
                ReleaseGate();
            }
        }

        if (result is null)
        {
            yield break;
        }

        if (result.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.Failure is { } failure)
        {
            throw new EdgeChatException(
                EdgeErrorCode.ChatGenerationFailed,
                FormattableString.Invariant(
                    $"ORT GenAI threw mid-decode after {result.Status.GeneratedTokens} token(s): {failure.Message}"),
                failure)
            {
                PresetId = _registration.Preset.Id,
                ModelId = ModelId,
                Remediation =
                    "The partial answer was streamed and the final update carries StopReason = Error " +
                    "with the real counts. Retry the turn; a repeat on the same prompt is a native " +
                    "fault worth reporting with the inner message.",
            };
        }
    }

    private async Task EnterGateAsync(CancellationToken cancellationToken)
    {
        var edge = _registration.Options;

        if (_gate.Wait(0, CancellationToken.None))
        {
            Interlocked.Increment(ref _gateEntries);
            return;
        }

        var depth = Interlocked.Increment(ref _queued);
        try
        {
            if (depth > edge.MaxQueuedTurns)
            {
                _statistics.TurnRejected();
                var full = ChatTurnPipeline.Busy(
                    $"{edge.MaxQueuedTurns} turn(s) are already queued behind the running one (EdgeChatOptions.MaxQueuedTurns)",
                    "Await the outstanding turns, or raise MaxQueuedTurns if a deeper queue is acceptable.");
                ChatTurnLog.TurnRejected(_logger, full.Message);
                throw full;
            }

            ChatTurnLog.TurnQueued(_logger, depth);

            if (!await _gate.WaitAsync(edge.TurnQueueTimeout, cancellationToken).ConfigureAwait(false))
            {
                _statistics.TurnRejected();
                var timedOut = ChatTurnPipeline.Busy(
                    $"the turn waited {edge.TurnQueueTimeout} for the model gate (EdgeChatOptions.TurnQueueTimeout)",
                    "Raise TurnQueueTimeout, or cancel the turn that is holding the gate.");
                ChatTurnLog.TurnRejected(_logger, timedOut.Message);
                throw timedOut;
            }

            Interlocked.Increment(ref _gateEntries);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    private void ReleaseGate()
    {
        try
        {
            _gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed under a running turn; there is nothing left to release into.
        }
    }

    private async Task<Turn> PrepareTurnAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        List<string> unhonoured,
        CancellationToken cancellationToken)
    {
        var edge = _registration.Options;
        var preset = _registration.Preset;
        var cacheEnabled = edge.EnableConversationCache;
        var requestedId = options?.ConversationId;

        // (f) Which generator. A null id is NEVER a hit; a different id is a miss.
        var previous = _cache.Take();
        var candidate = cacheEnabled
            && requestedId is not null
            && previous is not null
            && string.Equals(previous.ConversationId, requestedId, StringComparison.Ordinal)
                ? previous
                : null;

        if (previous is not null && candidate is null)
        {
            previous.Dispose();
        }

        var conversationId = cacheEnabled ? requestedId ?? Guid.NewGuid().ToString("N") : requestedId;

        ChatModelLease? freshLease = null;
        IChatGenerator? freshGenerator = null;

        try
        {
            ChatModelLease lease;
            IChatModelSession session;

            if (candidate is not null)
            {
                lease = candidate.Lease;
                session = candidate.Session;
            }
            else
            {
                freshLease = await _host.AcquireAsync(cancellationToken).ConfigureAwait(false);
                lease = freshLease;
                session = _sessionFactory(lease);
            }

            var info = lease.Info;
            var resolvedContext = info.ResolvedContextTokens;
            var knobs = ChatTurnPipeline.ResolveKnobs(options, edge);
            var maxOutput = knobs.MaxOutputTokens;
            var guidance = ChatTurnPipeline.ResolveGuidance(options, edge, info.Backend.Guidance, unhonoured);

            // (c) Reduce, counting with the model's own tokenizer.
            var composed = ChatTurnPipeline.ComposeMessages(messages, options, edge);
            var historyOptions = ChatTurnPipeline.HistoryOptionsForTurn(edge, resolvedContext, maxOutput);
            var reducer = new EdgeChatTokenBudgetReducer(
                text => text.Length == 0 ? 0 : session.Encode(text).Length,
                historyOptions);

            var retained = await reducer.ReduceAsync(composed, cancellationToken).ConfigureAwait(false);
            var reduced = retained as IReadOnlyList<ChatMessage> ?? [.. retained];
            var dropped = composed.Count - reduced.Count;

            if (dropped > 0)
            {
                ChatTurnLog.HistoryReduced(_logger, dropped, composed.Count, historyOptions.MaxHistoryTokens);
            }

            ChatTurnLog.ReducedMessages(_logger, ChatTurnPipeline.DescribeMessages(reduced));

            // (d) Format.
            var promptContext = new ChatPromptContext(session, preset.Id, info.Shape, resolvedContext);
            var fullText = Format(reduced, options, edge, info, promptContext);
            ChatTurnLog.FormattedPrompt(_logger, fullText);

            cancellationToken.ThrowIfCancellationRequested();

            // (f) Check one and check two, on a candidate. Either failing is a miss, never a 7102.
            if (candidate is not null && guidance is null
                && fullText.StartsWith(candidate.CachedText, StringComparison.Ordinal))
            {
                var delta = fullText[candidate.CachedText.Length..];
                var deltaTokens = delta.Length == 0 ? [] : session.Encode(delta);
                var held = candidate.Generator.TokenCount();

                if (held + (ulong)deltaTokens.Length + (ulong)maxOutput <= (ulong)candidate.MaxLength)
                {
                    // Prefill is not interruptible; it is bracketed instead.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deltaTokens.Length > 0)
                    {
                        candidate.Generator.AppendTokens(deltaTokens);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    return new Turn
                    {
                        Lease = lease,
                        Session = session,
                        Generator = candidate.Generator,
                        Stream = session.CreateStream(),
                        FullText = fullText,
                        ConversationId = conversationId,
                        Cacheable = true,
                        MaxLength = candidate.MaxLength,
                        MaxOutputTokens = maxOutput,
                        ResolvedContextTokens = resolvedContext,
                        PromptTokens = checked((int)candidate.Generator.TokenCount()),
                        PromptTokensAppended = deltaTokens.Length,
                        MessagesDropped = dropped,
                        GeneratorOutcome = "reused from the conversation cache",
                    };
                }
            }

            // A miss: the candidate's generator and lease go, and the turn builds fresh.
            var outcome = "built fresh";
            if (candidate is not null)
            {
                freshLease = await _host.AcquireAsync(cancellationToken).ConfigureAwait(false);
                lease = freshLease;
                session = _sessionFactory(lease);
                candidate.Dispose();
                candidate = null;
                outcome = "rebuilt after a conversation-cache miss";
            }

            var tokens = session.Encode(fullText);
            var promptTokens = tokens.Length;

            if (promptTokens + maxOutput > resolvedContext)
            {
                throw ChatTurnPipeline.PromptTooLong(preset.Id, promptTokens, resolvedContext, maxOutput);
            }

            // (e) max_length: the KV MEMORY cap, <= resolvedContext either way. A generator the
            // cache may keep is built to the budget's whole answer so later turns fit; one that is
            // used once is built to what this turn needs.
            var maxLength = cacheEnabled && guidance is null
                ? resolvedContext
                : Math.Min(promptTokens + maxOutput, resolvedContext);

            var searchOptions = ChatTurnPipeline.ComposeSearchOptions(knobs, maxLength, options, edge);

            cancellationToken.ThrowIfCancellationRequested();
            freshGenerator = session.CreateGenerator(searchOptions, guidance);
            freshGenerator.AppendTokens(tokens);
            cancellationToken.ThrowIfCancellationRequested();

            var turn = new Turn
            {
                Lease = lease,
                Session = session,
                Generator = freshGenerator,
                Stream = session.CreateStream(),
                FullText = fullText,
                ConversationId = conversationId,
                Cacheable = cacheEnabled && guidance is null && conversationId is not null,
                MaxLength = maxLength,
                MaxOutputTokens = maxOutput,
                ResolvedContextTokens = resolvedContext,
                PromptTokens = checked((int)freshGenerator.TokenCount()),
                PromptTokensAppended = promptTokens,
                MessagesDropped = dropped,
                GeneratorOutcome = outcome,
            };

            freshGenerator = null;
            freshLease = null;
            return turn;
        }
        catch
        {
            freshGenerator?.Dispose();
            freshLease?.Dispose();
            candidate?.Dispose();
            _cache.Release();
            throw;
        }
    }

    private static string Format(
        IReadOnlyList<ChatMessage> reduced,
        ChatOptions? options,
        EdgeChatOptions edge,
        ChatModelInfo info,
        ChatPromptContext context)
    {
        if (edge.PromptFormatter is { } formatter)
        {
            return formatter(reduced, options, context);
        }

        if (info.Backend.ChatTemplateSupported)
        {
            return context.ApplyChatTemplate(ChatTurnPipeline.MessagesToJson(reduced), addGenerationPrompt: true);
        }

        // The probe already refused when RequireChatTemplate was set; reaching here means the
        // consumer chose the materially worse fallback.
        return ChatTurnPipeline.FallbackFormat(reduced);
    }

    private void FinishTurn(Turn turn, ChatDecodeResult? result)
    {
        _host.SetActiveGeneration(null);
        turn.Stream.Dispose();

        var keep = result is { Cancelled: false, Failure: null }
            && result.Status.StopReason is EdgeChatStopReason.Completed
                or EdgeChatStopReason.StopSequence
                or EdgeChatStopReason.MaxOutputTokens
            && turn.Cacheable
            && !_host.TerminationRequested
            && !_disposed;

        if (keep)
        {
            var cachedText = turn.FullText + result!.DecodedText;
            var entry = new ConversationCache.Entry(
                turn.ConversationId!,
                turn.Lease,
                turn.Session,
                turn.Generator,
                cachedText,
                turn.MaxLength);

            if (_cache.Store(entry))
            {
                ChatTurnLog.CachedText(_logger, cachedText);
            }
        }
        else
        {
            turn.Generator.Dispose();
            turn.Lease.Dispose();
            _cache.Release();
        }

        if (result is not null)
        {
            _statistics.TurnCompleted(result.Status);
            ChatTurnLog.TurnCompleted(_logger, result.Status);
        }
    }

    /// <summary>Copies the turn record and the unhonoured-options list from the aggregated message onto the response.</summary>
    private static void HoistTurnMetadata(ChatResponse response)
    {
        for (var i = response.Messages.Count - 1; i >= 0; i--)
        {
            if (response.Messages[i].AdditionalProperties is not { } onMessage)
            {
                continue;
            }

            foreach (var key in new[] { EdgeChatProperties.TurnStatus, EdgeChatProperties.UnhonouredOptions })
            {
                if (onMessage.TryGetValue(key, out var value)
                    && (response.AdditionalProperties is null || !response.AdditionalProperties.ContainsKey(key)))
                {
                    (response.AdditionalProperties ??= [])[key] = value;
                }
            }

            return;
        }
    }

    private int UnloadEvents()
    {
        var info = _host.Describe();
        return info is { LoadCount: > 1 } ? info.LoadCount - 1 : 0;
    }

    private static void TryTerminate(IChatGenerator generator)
    {
        try
        {
            ChatTurnPipeline.Terminate(generator);
        }
#pragma warning disable CA1031 // A generator that has already finished cannot be terminated, and that is not a failure.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Everything one turn owns between prefill and the final update.</summary>
    private sealed class Turn
    {
        public required ChatModelLease Lease { get; init; }

        public required IChatModelSession Session { get; init; }

        public required IChatGenerator Generator { get; init; }

        public required IChatTokenStream Stream { get; init; }

        public required string FullText { get; init; }

        public required string? ConversationId { get; init; }

        public required bool Cacheable { get; init; }

        public required int MaxLength { get; init; }

        public required int MaxOutputTokens { get; init; }

        public required int ResolvedContextTokens { get; init; }

        public required int PromptTokens { get; init; }

        public required int PromptTokensAppended { get; init; }

        public required int MessagesDropped { get; init; }

        public required string GeneratorOutcome { get; init; }
    }
}
