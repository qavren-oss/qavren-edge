using Microsoft.ML.OnnxRuntimeGenAI;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// One generator, as the decode loop sees it. The five calls spec section 10(f) makes and nothing
/// else - no tensors, no adapters, no rewinding.
/// </summary>
/// <remarks>
/// An interface rather than the GenAI <c>Generator</c> so the decode loop is a tier-1 unit test
/// with no natives: <c>FakeGenerator</c> scripts the decoded fragments, records what was appended,
/// and throws on cue. The real one is <see cref="GenAiChatSession"/>'s inner class.
/// </remarks>
internal interface IChatGenerator : IDisposable
{
    /// <summary>Whether the model emitted end-of-sequence or <c>max_length</c> bound.</summary>
    /// <returns><see langword="true"/> when no further token can be generated.</returns>
    bool IsDone();

    /// <summary>Prefill: appends prompt tokens to the sequence. Not interruptible.</summary>
    /// <param name="tokens">The token ids.</param>
    void AppendTokens(ReadOnlySpan<int> tokens);

    /// <summary>The total sequence length the model has conditioned on so far.</summary>
    /// <returns>The count, as the native API reports it.</returns>
    ulong TokenCount();

    /// <summary>One decode step. Blocking, native, and never on the caller's thread.</summary>
    void GenerateNextToken();

    /// <summary>The token the last <see cref="GenerateNextToken"/> produced.</summary>
    /// <returns>The token id.</returns>
    int LastToken();

    /// <summary><c>terminate_session</c> is the only key the decode loop sets.</summary>
    /// <param name="key">The runtime option.</param>
    /// <param name="value">Its value.</param>
    void SetRuntimeOption(string key, string value);
}

/// <summary>
/// Answers <see cref="IChatGenerator.IsDone"/> in managed code once <c>terminate_session</c> has
/// been set, and serialises the cheap native calls against the setter.
/// </summary>
/// <remarks>
/// <para>
/// GenAI 0.15.2 wraps nearly every C entry point in <c>OGA_TRY</c>/<c>OGA_CATCH</c>, so a native
/// throw arrives as an <c>OgaResult</c> and then as an <c>OnnxRuntimeGenAIException</c>.
/// <c>OgaGenerator_IsDone</c> (<c>src/ort_genai_c.cpp:470</c>) is one of the handful that is NOT:
/// it is <c>return generator-&gt;IsDone();</c> with no handler. <c>Generator::IsDone</c>
/// (<c>src/generators.cpp:784</c>) opens with <c>ThrowErrorIfSessionTerminated</c>, which throws
/// <c>std::runtime_error("Session in Terminated state, exiting!")</c>
/// (<c>src/generators.cpp:51</c>). That C++ exception unwinds into the P/Invoke frame, finds no
/// handler, and the process aborts with <c>std::terminate</c> - not a catchable .NET exception.
/// </para>
/// <para>
/// <c>terminate_session</c> is set from another thread (a cancel registration, or
/// <c>TerminateActiveGeneration</c> under memory pressure or suspend), so "check a flag, then call
/// <c>IsDone</c>" is not enough on its own: the latch and <c>IsDone</c> take the same lock, and the
/// setter takes it too. <see cref="AppendTokens"/> and <see cref="GenerateNextToken"/> deliberately
/// do NOT - prefill and the decode step are the multi-second native calls that
/// <c>terminate_session</c> exists to interrupt (<c>ChatModelHost.TerminateActiveGeneration</c> is
/// "the only thing that can stop a multi-second prefill", and it may be called from a lifecycle
/// thread that must not block behind one), and both C entry points ARE wrapped, so a
/// terminated-state throw there surfaces as an <c>OnnxRuntimeGenAIException</c> the decode loop
/// already handles.
/// </para>
/// <para>
/// The latch tracks the VALUE: <c>"0"</c> clears it, which is how a generator reused from the
/// conversation cache starts its next turn clean (<c>ChatTurnPipeline.Resume</c>).
/// </para>
/// </remarks>
/// <param name="inner">The generator being guarded.</param>
internal sealed class TerminationLatchedGenerator(IChatGenerator inner) : IChatGenerator
{
    private readonly object _sync = new();
    private bool _terminated;
    private bool _disposed;

    /// <summary>Whether <c>terminate_session=1</c> has been seen and not cleared.</summary>
    public bool IsTerminated
    {
        get
        {
            lock (_sync)
            {
                return _terminated;
            }
        }
    }

    /// <inheritdoc />
    public bool IsDone()
    {
        lock (_sync)
        {
            // The latch, not the native call: see the remarks. A terminated generator is done as
            // far as the decode loop is concerned, and the loop reports the real reason from the
            // host's termination flags rather than from this answer.
            return _terminated || _disposed || inner.IsDone();
        }
    }

    /// <inheritdoc />
    /// <remarks>Outside the lock on purpose: see the class remarks. Interruptible by the setter.</remarks>
    public void AppendTokens(ReadOnlySpan<int> tokens) => inner.AppendTokens(tokens);

    /// <inheritdoc />
    public ulong TokenCount()
    {
        lock (_sync)
        {
            return inner.TokenCount();
        }
    }

    /// <inheritdoc />
    public void GenerateNextToken() => inner.GenerateNextToken();

    /// <inheritdoc />
    public int LastToken()
    {
        lock (_sync)
        {
            return inner.LastToken();
        }
    }

    /// <inheritdoc />
    public void SetRuntimeOption(string key, string value)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                // A terminate that lost the race with disposal. Calling into a freed native
                // generator is an access violation, not an exception.
                return;
            }

            if (string.Equals(key, "terminate_session", StringComparison.Ordinal))
            {
                // GenAI accepts "0" as well and clears the flag (src/models/model.cpp:149-160), so
                // the latch tracks the value rather than the fact of the call.
                _terminated = !string.Equals(value, "0", StringComparison.Ordinal);
            }

            inner.SetRuntimeOption(key, value);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            inner.Dispose();
        }
    }
}

/// <summary>A stateful token-to-text decoder, one per turn.</summary>
internal interface IChatTokenStream : IDisposable
{
    /// <summary>Decodes one token, carrying multi-byte state from the previous call.</summary>
    /// <param name="token">The token id.</param>
    /// <returns>The text this token completes, possibly empty.</returns>
    string Decode(int token);
}

/// <summary>A constrained-decoding request, passed to <c>GeneratorParams.SetGuidance</c>.</summary>
/// <param name="Type">The guidance type - <c>json_schema</c> for a <c>ChatResponseFormatJson</c>.</param>
/// <param name="Data">The schema text.</param>
internal readonly record struct ChatGuidance(string Type, string Data);

/// <summary>
/// What one turn needs from a loaded model: the template, the tokenizer's two directions, and a
/// generator factory. The whole native surface the turn pipeline reaches, behind one seam.
/// </summary>
internal interface IChatModelSession
{
    /// <summary>Runs the model's own Jinja template through minja.</summary>
    /// <param name="messagesJson">A JSON array of <c>{"role","content"}</c>.</param>
    /// <param name="addGenerationPrompt">Whether to append the assistant turn header.</param>
    /// <returns>The formatted prompt.</returns>
    string ApplyChatTemplate(string messagesJson, bool addGenerationPrompt);

    /// <summary>Encodes text with the model's own tokenizer.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The token ids.</returns>
    int[] Encode(string text);

    /// <summary>Creates a fresh decode stream for one turn.</summary>
    /// <returns>The stream; the turn disposes it.</returns>
    IChatTokenStream CreateStream();

    /// <summary>Builds a generator from the composed search options.</summary>
    /// <param name="searchOptions">
    /// The options, in application order. Every value is a <see cref="bool"/> or a
    /// <see cref="double"/> - the pipeline has already converted or refused everything else.
    /// </param>
    /// <param name="guidance">The constrained-decoding request, or null.</param>
    /// <returns>The generator; the turn or the conversation cache disposes it.</returns>
    IChatGenerator CreateGenerator(IReadOnlyList<KeyValuePair<string, object>> searchOptions, ChatGuidance? guidance);
}

/// <summary>Builds the session for a lease.</summary>
/// <param name="lease">The borrowed model.</param>
/// <returns>The session.</returns>
/// <remarks>The tests inject a fake; the registration passes <see cref="GenAiChatSession.Create"/>.</remarks>
internal delegate IChatModelSession ChatSessionFactory(ChatModelLease lease);

/// <summary>
/// The host as the client sees it: <see cref="IChatModelHost"/> plus the four members the decode
/// loop and the conversation cache exchange with the lifecycle observer through it.
/// </summary>
internal interface IChatTurnHost : IChatModelHost
{
    /// <summary>Whether <see cref="IChatModelHost.TerminateActiveGeneration"/> has been called since the last turn began.</summary>
    bool TerminationRequested { get; }

    /// <summary>Whether the OS is suspending the app, so a termination reads as <c>Suspended</c>.</summary>
    bool SuspendRequested { get; }

    /// <summary>Registers the live generator's <c>SetRuntimeOption</c>. Null clears it.</summary>
    /// <param name="setRuntimeOption">The setter, or null.</param>
    void SetActiveGeneration(Action<string, string>? setRuntimeOption);

    /// <summary>Registers the conversation cache's drop. Null clears it.</summary>
    /// <param name="drop">The drop, or null.</param>
    void SetConversationCache(Action? drop);
}

/// <summary>The real session over a <see cref="ChatModelLease"/>.</summary>
/// <param name="lease">The borrowed model.</param>
internal sealed class GenAiChatSession(ChatModelLease lease) : IChatModelSession
{
    /// <summary>The <see cref="ChatSessionFactory"/> the registration uses.</summary>
    /// <param name="lease">The borrowed model.</param>
    /// <returns>A session over it.</returns>
    public static IChatModelSession Create(ChatModelLease lease) => new GenAiChatSession(lease);

    /// <inheritdoc />
    public string ApplyChatTemplate(string messagesJson, bool addGenerationPrompt) =>
        lease.Tokenizer.ApplyChatTemplate(null!, messagesJson, null!, addGenerationPrompt);

    /// <inheritdoc />
    public int[] Encode(string text)
    {
        using var sequences = lease.Tokenizer.Encode(text);
        return sequences[0].ToArray();
    }

    /// <inheritdoc />
    public IChatTokenStream CreateStream() => new Stream(lease.Tokenizer.CreateStream());

    /// <inheritdoc />
    public IChatGenerator CreateGenerator(
        IReadOnlyList<KeyValuePair<string, object>> searchOptions,
        ChatGuidance? guidance)
    {
        GeneratorParams? parameters = null;
        try
        {
            parameters = new GeneratorParams(lease.Model);

            foreach (var (key, value) in searchOptions)
            {
                switch (value)
                {
                    case bool flag:
                        parameters.SetSearchOption(key, flag);
                        break;
                    case double number:
                        parameters.SetSearchOption(key, number);
                        break;
                    default:
                        // The pipeline converts or refuses before this point; reaching here is a
                        // bug in the pipeline, not a caller error.
                        throw new InvalidOperationException(
                            $"Search option '{key}' reached the generator as {value?.GetType().Name ?? "null"}.");
                }
            }

            if (guidance is { } request)
            {
                // TWO arguments. enableFFTokens stays at its default (plan adjustment 3).
                parameters.SetGuidance(request.Type, request.Data);
            }

            return new TerminationLatchedGenerator(
                new NativeGenerator(new Generator(lease.Model, parameters), parameters));
        }
        catch
        {
            parameters?.Dispose();
            throw;
        }
    }

    private sealed class Stream(TokenizerStream inner) : IChatTokenStream
    {
        public string Decode(int token) => inner.Decode(token);

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// The generator and the parameters it was built from, disposed together. The native call
    /// consumes the parameters at construction, but every GenAI wrapper carries a finalizer and
    /// holding both until the generator goes is the arrangement that cannot be wrong.
    /// </summary>
    private sealed class NativeGenerator(Generator generator, GeneratorParams parameters) : IChatGenerator
    {
        public bool IsDone() => generator.IsDone();

        public void AppendTokens(ReadOnlySpan<int> tokens) => generator.AppendTokens(tokens);

        public ulong TokenCount() => generator.TokenCount();

        public void GenerateNextToken() => generator.GenerateNextToken();

        public int LastToken()
        {
            var sequence = generator.GetSequence(0);
            return sequence.Length == 0 ? -1 : sequence[^1];
        }

        public void SetRuntimeOption(string key, string value) => generator.SetRuntimeOption(key, value);

        public void Dispose()
        {
            generator.Dispose();
            parameters.Dispose();
        }
    }
}
