using Microsoft.Extensions.AI;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat;

/// <summary>How constrained decoding is treated.</summary>
public enum EdgeGuidancePolicy
{
    /// <summary>
    /// Default. A <c>ChatResponseFormatJson</c> is refused with
    /// <see cref="EdgeErrorCode.ChatGuidanceUnavailable"/>, never silently ignored.
    /// <para>
    /// The refusal is scoped to <c>ChatResponseFormatJson</c> and to nothing else.
    /// <c>ChatResponseFormat.Text</c> is a non-null value that asks for plain text, needs no
    /// constrained decoding, and is already what this client does - refusing it would fail a
    /// caller for requesting the default behaviour. A null <c>ResponseFormat</c> is likewise
    /// untouched. Upstream's own client maps only <c>ChatResponseFormatJson</c> onto
    /// <c>SetGuidance</c>, and this is the same boundary drawn honestly instead of silently.
    /// </para>
    /// </summary>
    Disabled,

    /// <summary>
    /// Probe once per model; apply <c>SetGuidance</c> only when the probe proves the constraint is
    /// enforced, otherwise fall through unconstrained and record it.
    /// </summary>
    PreferNative,

    /// <summary>
    /// Probe once; throw <see cref="EdgeErrorCode.ChatGuidanceUnavailable"/> when the probe does
    /// not prove enforcement.
    /// </summary>
    RequireNative,
}

/// <summary>The guidance probe's outcome.</summary>
/// <remarks>
/// <see cref="NotEnforced"/> means "not proven enforced", <b>not</b> "proven absent": a
/// guidance-enabled build could also fail the probe if the model or its template is unusual, which
/// is why the taxonomy separates a throw (<see cref="ProbeFailed"/>) from a mismatch. The
/// diagnostics key says exactly that.
/// </remarks>
public enum EdgeGuidanceProbeResult
{
    /// <summary>The probe has not run.</summary>
    Unprobed,

    /// <summary>The probe proved the constraint was applied.</summary>
    Enforced,

    /// <summary>The probe ran and the constraint was not proven enforced.</summary>
    NotEnforced,

    /// <summary>The probe itself threw.</summary>
    ProbeFailed,
}

/// <summary>How thermal pressure paces or stops a turn.</summary>
public sealed class ChatThermalOptions
{
    /// <summary>At or above this, pace the decode to <see cref="ThrottledTokensPerSecond"/>. Default <c>Serious</c>.</summary>
    public EdgeThermalState ThrottleAt { get; set; } = EdgeThermalState.Serious;

    /// <summary>At or above this, terminate the turn. Default <c>Critical</c>.</summary>
    public EdgeThermalState AbortAt { get; set; } = EdgeThermalState.Critical;

    /// <summary>Android's headroom, where 1.0 <i>is</i> the documented SEVERE threshold. Null disables.</summary>
    public float? ThrottleHeadroom { get; set; } = 1.0f;

    /// <summary>The paced rate. Default 8 tok/s - readable, and a visibly slow answer beats a dead one.</summary>
    public double ThrottledTokensPerSecond { get; set; } = 8.0;

    /// <summary>
    /// How often the monitor is re-read mid-decode. Default 1 s, which is also sub-project 2's
    /// monitor cache window and Android's <c>GetThermalHeadroom</c> rate limit - so sampling costs
    /// nothing beyond the cached read.
    /// </summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Refuse to START a turn in low-power mode. Default false: that is a product decision, not a safety one.</summary>
    public bool RefuseNewTurnsInLowPowerMode { get; set; }

    /// <summary>
    /// Refuse to start a turn when <c>EdgeResourceSnapshot.Thermal</c> is
    /// <see cref="EdgeThermalState.Unknown"/>. <b>Default false</b>, and that is a decision rather
    /// than an accident of enum ordering.
    /// <para>
    /// Sub-project 2 numbers <c>Unknown = 0</c>, below <c>Nominal</c>, and its own doc says "the
    /// platform reports nothing usable. Never treat this as &quot;fine&quot;." A bare
    /// <c>state &gt;= ThrottleAt</c> would treat it as better than fine - never throttling, never
    /// aborting - on every desktop, every hosted runner, and every Android device below API 29
    /// where the thermal API is unavailable. So the comparisons in the decode loop are written
    /// explicitly as <c>thermal != Unknown &amp;&amp; thermal &gt;= threshold</c>, with the intent
    /// in the code rather than in the enum's numbering.
    /// </para>
    /// <para>
    /// Having made it explicit, the default is still "do not refuse", for the same reason the
    /// memory gate skips an unreadable <c>AvailableMemoryBytes</c> rather than refusing on it: a
    /// reading nobody can make is never a refusal, and refusing here would make chat unusable on
    /// every desktop. What sub-project 4 owes instead is visibility - <c>Unknown</c> is published
    /// verbatim in the <c>thermalState</c> diagnostics key and in <c>ChatTurnStatus.Thermal</c>,
    /// never normalised to <c>Nominal</c> - and this switch, for a caller who has measured their
    /// hardware and wants the opposite.
    /// </para>
    /// <para>
    /// <see cref="ThrottleHeadroom"/> gets the same treatment for free: Android returns
    /// <c>NaN</c> when it is polled faster than about once a second or when the device does not
    /// support it, and <c>NaN &gt;= 1.0f</c> is <c>false</c>, so an unreadable headroom never
    /// throttles. That is the same rule reached by a different route, and it is stated so nobody
    /// "fixes" it.
    /// </para>
    /// </summary>
    public bool RefuseWhenThermalUnknown { get; set; }
}

/// <summary>What history reduction preserves and what it may evict.</summary>
public sealed class ChatHistoryOptions
{
    /// <summary>The most turns to keep, counting a user message and the reply it drew as one.</summary>
    public int MaxTurns { get; set; } = 8;

    /// <summary>The token budget history is reduced to fit.</summary>
    public int MaxHistoryTokens { get; set; } = 1024;

    /// <summary>Whether a <c>ChatRole.System</c> message is exempt from eviction.</summary>
    public bool PreserveSystemMessages { get; set; } = true;

    /// <summary>Never reduce below this many trailing messages, whatever the budget says.</summary>
    public int MinimumPreservedMessages { get; set; } = 2;

    /// <summary>
    /// A message whose <c>AdditionalProperties</c> carries any of these keys is <b>pinned</b> and
    /// is never evicted, whatever the budget says. Default: one entry,
    /// <c>"qavren.edge.rag.context"</c>.
    /// <para>
    /// That literal is <c>Qavren.Edge.Rag</c>'s <c>RagCitations.ContextMessagePropertyKey</c>,
    /// duplicated here rather than referenced, because this package must not depend on that one.
    /// A tier-1 test asserts the two strings are equal - the same deliberate duplication, with the
    /// same guard, that sub-project 2 uses for its two query-generator service keys.
    /// </para>
    /// <para>
    /// Pinning is what keeps the RAG block out of the eviction set. Without it the injected
    /// context is an unpaired user message at the tail, the reducer's "evict whole groups" rule has
    /// no group to put it in, and the first thing a tight budget would throw away is the grounding.
    /// </para>
    /// </summary>
    public IList<string> PinnedMessageKeys { get; } = new List<string> { "qavren.edge.rag.context" };
}

/// <summary>What provisioning checks before it moves a gigabyte.</summary>
public sealed class ChatProvisioningOptions
{
    /// <summary>
    /// Free disk required beyond the bytes still to transfer, checked against the <b>bundle
    /// total</b> before the first byte. Sub-project 2's HTTP source checks per file against a
    /// 32 MiB margin, which is the right question for a 23 MiB embedding graph and the wrong one
    /// for a 1.24 GB-decimal folder. Default 512 MiB.
    /// </summary>
    public long FreeDiskMarginBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Consulted once, immediately before the first byte of a transfer. Returning false makes
    /// <c>ProvisionAsync</c> throw <see cref="EdgeErrorCode.ChatDownloadNotPermitted"/> without
    /// opening a connection. Null (the default) permits the transfer.
    /// <para>
    /// <b>This is a hook and not a network stack, deliberately.</b> Reading whether the connection
    /// is metered means <c>Microsoft.Maui.Networking.Connectivity</c> on MAUI,
    /// <c>NetworkInterface</c>/<c>NWPathMonitor</c> elsewhere, and a per-platform answer that does
    /// not exist on a bare <c>ServiceCollection</c> - which the suite's decisions say this package
    /// must run on. So the policy is one line in the app:
    /// <c>o.IsTransferPermitted = () =&gt; Connectivity.Current.ConnectionProfile == ConnectionProfile.WiFi;</c>
    /// and the sample app shows exactly that. Google Play warns a user above 200 MB on mobile data
    /// and both shipped presets are far past it, so an app that ships chat and never sets this has
    /// made a decision, which is why the consent sheet names the transfer size.
    /// </para>
    /// </summary>
    public Func<bool>? IsTransferPermitted { get; set; }
}

/// <summary>Everything one registered chat client reads.</summary>
public sealed class EdgeChatOptions
{
    /// <summary>Set by <c>AddOnnxChat(preset, …)</c>. The callback may read it; replacing it is unsupported.</summary>
    public ChatPreset Preset { get; internal set; } = null!;

    /// <summary>An already-staged model directory, bypassing provisioning. Tests and the nightly lane.</summary>
    public string? ModelDirectoryOverride { get; set; }

    /// <summary>
    /// Null derives the value from the budget, capped by the preset's default and by
    /// <c>Shape.ContextLength</c>. Setting it turns the budget into a refusal rather than a cap: a
    /// value that does not fit throws <see cref="EdgeErrorCode.ChatInsufficientMemory"/> carrying
    /// the largest context that would have fit.
    /// </summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>
    /// Null falls through to <c>Preset.DefaultMaxOutputTokens</c>. A per-call
    /// <c>ChatOptions.MaxOutputTokens</c> beats both.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// Null falls through to <c>Preset.DefaultTemperature</c>; a per-call
    /// <c>ChatOptions.Temperature</c> beats both.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>Null falls through to <c>Preset.DefaultTopP</c>; <c>ChatOptions.TopP</c> beats both.</summary>
    public float? TopP { get; set; }

    /// <summary>Null falls through to <c>Preset.DefaultTopK</c>; <c>ChatOptions.TopK</c> beats both.</summary>
    public int? TopK { get; set; }

    /// <summary>Tokens held back from the prompt so a reply always has room. Default 64.</summary>
    public int ReservedPromptTokens { get; set; } = 64;

    /// <summary>Becomes a leading <c>ChatRole.System</c> message; nothing is dropped.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// <b>Added to</b> <c>Preset.StopSequences</c>, never replacing them: stop sequences are a
    /// property of the model's template as much as of the caller's intent, and a consumer adding
    /// <c>"\nUser:"</c> must not thereby delete the preset's <c>&lt;|eot_id|&gt;</c>. The effective
    /// set is the ordinal-distinct union of the preset's, these, and
    /// <c>ChatOptions.StopSequences</c>, sorted longest-first.
    /// </summary>
    public IList<string> StopSequences { get; } = new List<string>();

    /// <summary>
    /// Overrides <c>Tokenizer.ApplyChatTemplate</c>. The escape hatch for a model whose Jinja
    /// template minja cannot parse - which surfaces on the FIRST <c>ApplyChatTemplate</c> call and
    /// not at load, which is why the warm-up task probes it.
    /// </summary>
    public ChatPromptFormatter? PromptFormatter { get; set; }

    /// <summary>Throw <see cref="EdgeErrorCode.ChatTemplateUnsupported"/> rather than falling back. Default true.</summary>
    public bool RequireChatTemplate { get; set; } = true;

    /// <summary>How a <c>ChatResponseFormatJson</c> request is treated. Default <see cref="EdgeGuidancePolicy.Disabled"/>.</summary>
    public EdgeGuidancePolicy Guidance { get; set; } = EdgeGuidancePolicy.Disabled;

    /// <summary>
    /// Keep ONE <c>Generator</c> alive between turns, keyed by <c>ChatOptions.ConversationId</c>,
    /// so a follow-up skips prefill.
    /// <para>
    /// <b>A null id never hits the cache.</b> Not "matches the cached null" - never hits. Two
    /// unrelated conversations that both leave the field null would otherwise share one KV cache
    /// and therefore one history, which is a correctness bug wearing a performance optimisation's
    /// clothes. Null means "build me a fresh generator and tell me its id".
    /// </para>
    /// <para>
    /// <b>A cached generator is built once with <c>max_length = resolvedContext</c></b> - the
    /// budget's answer, and the KV memory cap - while the per-turn output cap is a managed
    /// generated-token counter in the decode loop. <c>SetSearchOption</c> exists only on
    /// <c>GeneratorParams</c>, which is consumed at <c>Generator</c> construction, so "set
    /// max_length on every turn including a cached one" is not implementable and sub-project 4
    /// does not pretend to.
    /// </para>
    /// </summary>
    public bool EnableConversationCache { get; set; } = true;

    /// <summary>
    /// Drop the cached <c>Generator</c> - not the model - on <c>Sleeping</c>. Default true: the KV
    /// cache is the half that is cheap to rebuild and expensive to be killed for.
    /// </summary>
    public bool DropConversationCacheOnSleep { get; set; } = true;

    /// <summary>Dispose the model itself on <c>MemoryPressure(Critical)</c>. Default true.</summary>
    public bool DropOnMemoryPressure { get; set; } = true;

    /// <summary>
    /// Unload on <c>Sleeping</c>. <b>Default false</b>: an app backgrounded for two seconds should
    /// not pay a 1.6 s reload, and <c>Sleeping</c> already stops the in-flight turn and drops the
    /// KV cache either way.
    /// </summary>
    public bool UnloadOnSleeping { get; set; }

    /// <summary>Turns beyond this waiting for the model gate are refused with <see cref="EdgeErrorCode.ChatBusy"/>. Default 4.</summary>
    public int MaxQueuedTurns { get; set; } = 4;

    /// <summary>How long a queued turn waits for the gate. Default 30 s.</summary>
    public TimeSpan TurnQueueTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Bounds <c>new Model(config)</c> plus tokenizer construction. Default 2 minutes.</summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Raw <c>GeneratorParams.SetSearchOption</c> entries, applied last so they win over everything
    /// mapped from <c>ChatOptions</c>.
    /// <para>
    /// <b>The native API has exactly two overloads</b> - <c>SetSearchOption(string, double)</c> and
    /// <c>SetSearchOption(string, bool)</c> - so this dictionary's values are converted or refused,
    /// never quietly dropped. That is deliberately the opposite of the rule for
    /// <c>ChatOptions.AdditionalProperties</c>, where an unrecognised value is ignored without
    /// error: that one is an open bag shared with the rest of the MEAI pipeline, while this one
    /// exists for exactly one purpose and nothing else writes to it, so a <c>"0.7"</c> typed as a
    /// string is a typo - and a typo in the escape hatch must not become a temperature that was
    /// never applied.
    /// </para>
    /// <para>
    /// <c>max_length</c> is refused whatever its type: it is the memory cap, and the whole gate
    /// depends on the generator getting the value the budget chose.
    /// </para>
    /// </summary>
    public IDictionary<string, object> SearchOptions { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

    /// <summary>
    /// <c>Config.Overlay(json)</c>, applied last before load. The whole execution-provider escape
    /// hatch, and deliberately the only one: GenAI names providers as strings inside
    /// <c>genai_config.json</c> and has no CoreML and no NNAPI provider, so a typed policy object
    /// would model choices that do not exist on any platform this suite ships to. Setting any
    /// provider other than CPU on a mobile TFM is refused with
    /// <see cref="EdgeErrorCode.ChatExecutionProviderUnsupported"/> naming the provider, rather
    /// than being silently ignored by the native config parser.
    /// </summary>
    public string? ConfigOverlayJson { get; set; }

    /// <summary>The memory gate's constants.</summary>
    public ChatMemoryBudgetOptions Memory { get; } = new();

    /// <summary>Thermal pacing and refusal.</summary>
    public ChatThermalOptions Thermal { get; } = new();

    /// <summary>History reduction.</summary>
    public ChatHistoryOptions History { get; } = new();

    /// <summary>Provisioning pre-flight.</summary>
    public ChatProvisioningOptions Provisioning { get; } = new();

    /// <summary>
    /// The effective stop-sequence set: the ordinal-distinct union of the preset's, these, and the
    /// per-call <c>ChatOptions.StopSequences</c>, in that order, then sorted <b>longest-first</b> so
    /// the rolling matcher in the decode loop always reports the longest match at a given position.
    /// </summary>
    /// <param name="preset">The preset whose template declares the model's own stop strings.</param>
    /// <param name="options">This client's additions, or null.</param>
    /// <param name="perCall">The per-call additions, or null.</param>
    /// <returns>The union, longest-first, insertion-ordered within one length.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="preset"/> is null.</exception>
    internal static IReadOnlyList<string> ComposeStopSequences(
        ChatPreset preset,
        EdgeChatOptions? options,
        IEnumerable<string>? perCall)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var union = new List<string>();

        Add(preset.StopSequences);
        Add(options?.StopSequences);
        Add(perCall);

        // OrderByDescending is a stable sort, so entries of equal length keep the order they were
        // added in - preset first, which is what makes "a caller adding one must not delete the
        // model's own" observable rather than merely intended.
        return [.. union.OrderByDescending(static s => s.Length)];

        void Add(IEnumerable<string>? source)
        {
            if (source is null)
            {
                return;
            }

            foreach (var sequence in source)
            {
                if (!string.IsNullOrEmpty(sequence) && seen.Add(sequence))
                {
                    union.Add(sequence);
                }
            }
        }
    }
}

/// <summary>Builds the prompt string handed to the tokenizer.</summary>
/// <param name="messages">The reduced history, system prompt already folded in.</param>
/// <param name="options">The per-call options, or null.</param>
/// <param name="context">What the formatter may reach.</param>
/// <returns>The prompt text.</returns>
public delegate string ChatPromptFormatter(
    IReadOnlyList<ChatMessage> messages, ChatOptions? options, IChatPromptContext context);

/// <summary>
/// What a formatter may reach. Deliberately not the raw <c>Tokenizer</c>: the GenAI C API is
/// documented as not thread safe and the decode loop owns the gate.
/// </summary>
public interface IChatPromptContext
{
    /// <summary>Runs the model's own Jinja template through minja.</summary>
    /// <param name="messagesJson">A JSON array of <c>{"role","content"}</c>.</param>
    /// <param name="addGenerationPrompt">Whether to append the assistant turn header.</param>
    /// <returns>The formatted prompt.</returns>
    /// <exception cref="EdgeChatException"><see cref="EdgeErrorCode.ChatTemplateUnsupported"/>.</exception>
    string ApplyChatTemplate(string messagesJson, bool addGenerationPrompt);

    /// <summary>Counts tokens with the model's own tokenizer, never an estimate.</summary>
    /// <param name="text">The text to count.</param>
    /// <returns>The token count.</returns>
    int CountTokens(string text);

    /// <summary>The loaded model's geometry.</summary>
    ChatModelShape Shape { get; }

    /// <summary>The context the budget allowed, which may be below the preset's default.</summary>
    int ResolvedContextTokens { get; }
}
