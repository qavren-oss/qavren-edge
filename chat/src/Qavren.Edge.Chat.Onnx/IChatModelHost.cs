using Microsoft.ML.OnnxRuntimeGenAI;

namespace Qavren.Edge.Chat;

/// <summary>What the shipped native build and this model actually turned out to be.</summary>
/// <param name="GenAiVersion">
/// The <c>Microsoft.ML.OnnxRuntimeGenAI</c> package version, taken from the assembly metadata the
/// csproj emits out of the central package pin. <b>Not</b> read from the GenAI assembly: it reports
/// <c>AssemblyVersion</c> 0.0.0.0, <c>FileVersion</c> 0.0.0.0, carries no
/// <c>AssemblyInformationalVersionAttribute</c>, and its managed surface has no version API
/// (measured - plan adjustment 1).
/// </param>
/// <param name="OrtVersion">The ONNX Runtime assembly version loaded in this process.</param>
/// <param name="RuntimeIdentifier">The running RID.</param>
/// <param name="Abi">The running Android ABI, or null off Android.</param>
/// <param name="Providers">The execution providers named in the resolved configuration.</param>
/// <param name="ChatTemplateSupported">
/// Whether minja parsed the model's own Jinja template. False is not a guess:
/// <c>TokenizerImpl::LoadChatTemplate</c> returns OK with only a warning when it cannot, so the
/// failure would otherwise surface mid-sentence on a user's first message.
/// </param>
/// <param name="Guidance">
/// The guidance probe's verdict. <see cref="EdgeGuidanceProbeResult.NotEnforced"/> means <i>not
/// proven enforced</i> and <b>not</b> <i>proven absent</i>.
/// </param>
/// <param name="PromptFormatter">
/// Which formatter this model's prompts go through: the model's own chat template, the consumer's
/// <see cref="ChatPromptFormatter"/>, or the role-prefixed fallback whose output quality is
/// materially worse.
/// </param>
public sealed record ChatBackendReport(
    string GenAiVersion,
    string OrtVersion,
    string RuntimeIdentifier,
    string? Abi,
    IReadOnlyList<string> Providers,
    bool ChatTemplateSupported,
    EdgeGuidanceProbeResult Guidance,
    string PromptFormatter);

/// <summary>Everything the host knows about the model it is holding.</summary>
/// <param name="PresetId">The preset that was loaded.</param>
/// <param name="ModelId">Its manifest's model id.</param>
/// <param name="Directory">The provisioned directory handed to <c>new Config(string)</c>.</param>
/// <param name="DirectorySha16">The 16-character content-addressed directory segment.</param>
/// <param name="Shape">The geometry read from the provisioned <c>genai_config.json</c>.</param>
/// <param name="Budget">Every term the memory gate read, and which one bound.</param>
/// <param name="ResolvedContextTokens">The context the budget allowed.</param>
/// <param name="Backend">The native build and this model's two probe results.</param>
/// <param name="LoadDuration">How long <c>new Model</c> plus <c>new Tokenizer</c> took.</param>
/// <param name="LoadedAtUtc">When the load completed.</param>
/// <param name="LoadCount">How many times this host has loaded, including drops and reloads.</param>
/// <param name="ActiveLeases">Outstanding leases.</param>
/// <param name="IsLoaded">Whether the model is resident right now.</param>
public sealed record ChatModelInfo(
    string PresetId,
    string ModelId,
    string Directory,
    string DirectorySha16,
    ChatModelShape Shape,
    ChatMemoryDecision Budget,
    int ResolvedContextTokens,
    ChatBackendReport Backend,
    TimeSpan LoadDuration,
    DateTimeOffset LoadedAtUtc,
    int LoadCount,
    int ActiveLeases,
    bool IsLoaded)
{
    /// <summary>
    /// <c>model.decoder.session_options.intra_op_num_threads</c> as the provisioned config declared
    /// it, or null when it declared nothing. Published as section 14.3's
    /// <c>chatIntraOpNumThreads</c>.
    /// </summary>
    /// <remarks>
    /// Carried here rather than on <see cref="ChatModelShape"/> (plan adjustment 29): every field
    /// on the shape is cross-checked against the provisioned config and a disagreement is
    /// <see cref="EdgeErrorCode.ChatModelShapeMismatch"/>, and a thread count is a runtime hint a
    /// publisher may change between revisions without changing the model. It is read once, from the
    /// config text the load path already had in hand, so the diagnostics contributor re-parses
    /// nothing.
    /// </remarks>
    public int? IntraOpNumThreads { get; init; }
}

/// <summary>
/// A borrowed model. <b>Exclusive</b> for the duration of one turn - there is no shared-reader
/// mode, because the GenAI C API is not thread safe and a second generator is a second full KV
/// cache. Disposing releases the lease; the <see cref="Model"/> and <see cref="Tokenizer"/> are
/// disposed only when the last lease returns after a drop. This is sub-project 2's
/// <c>OnnxSessionLease</c> contract and it exists for the same reason: every ORT GenAI wrapper type
/// is <see cref="IDisposable"/> <i>with a finalizer</i>, so a dropped-but-undisposed model pins the
/// whole native graph until GC - jetsam bait - while disposing one under a live
/// <c>GenerateNextToken</c> is a native access violation.
/// </summary>
public sealed class ChatModelLease : IDisposable
{
    private Action? _release;

    /// <summary>Creates the lease. Only the host calls this.</summary>
    /// <param name="model">The loaded model.</param>
    /// <param name="tokenizer">Its tokenizer.</param>
    /// <param name="config">The configuration the model was built from.</param>
    /// <param name="info">What the host knows about it.</param>
    /// <param name="release">Returns the lease. Called at most once.</param>
    internal ChatModelLease(Model model, Tokenizer tokenizer, Config config, ChatModelInfo info, Action release)
    {
        Model = model;
        Tokenizer = tokenizer;
        Config = config;
        Info = info;
        _release = release;
    }

    /// <summary>The loaded model. Valid until this lease is disposed and never after.</summary>
    public Model Model { get; }

    /// <summary>Its tokenizer.</summary>
    public Tokenizer Tokenizer { get; }

    /// <summary>The configuration the model was built from, overlay already applied.</summary>
    public Config Config { get; }

    /// <summary>What the host knows about the model, as of the moment the lease was taken.</summary>
    public ChatModelInfo Info { get; }

    /// <summary>Releases the lease. Idempotent.</summary>
    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        release?.Invoke();
    }
}

/// <summary>The ref-counted chat-model host. One per <c>AddOnnxChat</c> registration.</summary>
public interface IChatModelHost
{
    /// <summary>
    /// Awaits <c>IEdgeHost.EnsureStartedAsync</c>, verifies provisioning, resolves the budget,
    /// loads on first call, and returns the exclusive lease.
    /// </summary>
    /// <param name="cancellationToken">Cancellation. Never converted into another exception.</param>
    /// <returns>A lease that must be disposed.</returns>
    ValueTask<ChatModelLease> AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything <see cref="AcquireAsync"/> does <b>except</b> awaiting host startup, so an
    /// <c>IEdgeStartupTask</c> can drive a load without deadlocking on its own completion. Public
    /// on purpose: sub-project 2 documents this exact hole as unclosable from L1 because its
    /// equivalent is <c>internal</c> to another assembly. Sub-project 4's host and its warm-up task
    /// are in the same assembly, so sub-project 4 does not inherit it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes once the model is resident.</returns>
    ValueTask PreloadAsync(CancellationToken cancellationToken = default);

    /// <summary>What the host last knew about the model, or null if it has never loaded one.</summary>
    /// <returns>The model description, or null.</returns>
    ChatModelInfo? Describe();

    /// <summary>Marks the model for drop and disposes it once the lease returns.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> when a loaded model was marked or disposed.</returns>
    Task<bool> UnloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a volatile abort flag AND calls
    /// <c>Generator.SetRuntimeOption("terminate_session", "1")</c> on the live generator - the
    /// documented cooperative abort, and the only thing that can stop a multi-second prefill. Safe
    /// to call from a lifecycle observer on another thread; it is the one GenAI call sub-project 4
    /// makes outside the turn gate, and section 20 records that as a stated risk.
    /// </summary>
    void TerminateActiveGeneration();

    /// <summary>False between <c>MemoryPressure(Moderate)</c> and the next <c>Resumed</c>.</summary>
    bool IsAcceptingTurns { get; }
}
