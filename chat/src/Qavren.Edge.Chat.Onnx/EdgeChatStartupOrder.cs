namespace Qavren.Edge.Chat;

/// <summary>
/// 400-999 is free between sub-project 2's <c>VectorSchema</c> (300) and sub-project 1's
/// <c>ConsumerDefault</c> (1000). Sub-project 4 does not squat in sub-project 2's band.
/// </summary>
public static class EdgeChatStartupOrder
{
    /// <summary>
    /// Creates the one process-wide <c>OgaHandle</c>, disables telemetry, and checks the RID
    /// and ABI. <b>Must run after <c>EdgeAiStartupOrder.OnnxEnvironment</c> (200)</b>: ORT GenAI
    /// creates ORT's process-wide <c>OrtEnv</c> from NATIVE code on its first call
    /// (<c>OrtGlobals::OrtGlobals() : env_{OrtEnv::Create(...)}</c>), and sub-project 2's managed
    /// <c>OrtEnv.IsCreated</c> guard cannot see that - so touching any GenAI type before order 200
    /// silently costs sub-project 2 its log id, its severity and its <c>DOrtLoggingFunction</c>
    /// bridge, with no error anywhere. See spec section 9.1.
    /// </summary>
    public const int ChatEnvironment = 400;

    /// <summary>Opt-in. Verifies presence. Never downloads.</summary>
    public const int ChatModelProvisioning = 410;

    /// <summary>Opt-in. Loads the model, probes the chat template, optionally probes guidance.</summary>
    public const int ChatWarmUp = 420;
}

/// <summary>
/// 900-959 is sub-project 4's chat range. Sub-project 1's <c>EdgeEventIds</c> is a non-partial
/// static class and sub-project 2's <c>EdgeAiEventIds</c> owns 600-899, so sub-project 4
/// publishes its own and continues the numbering.
/// </summary>
public static class EdgeChatEventIds
{
    /// <summary>The process-wide <c>OgaHandle</c> was created.</summary>
    public const int GenAiRuntimeInitialized = 900;

    /// <summary>That handle was disposed, which calls <c>OgaShutdown()</c>.</summary>
    public const int GenAiRuntimeShutdown = 901;

    /// <summary><c>Utils.DisableTelemetryEvents()</c> was called.</summary>
    public const int GenAiTelemetryDisabled = 902;

    /// <summary><c>OrtEnv.IsCreated</c> was false when sub-project 4's order-400 task ran.</summary>
    public const int GenAiEnvironmentOutOfOrder = 903;

    /// <summary>A chat model and its tokenizer are loaded.</summary>
    public const int ChatModelLoaded = 910;

    /// <summary>A chat model could not be loaded.</summary>
    public const int ChatModelLoadFailed = 911;

    /// <summary>A loaded chat model was disposed.</summary>
    public const int ChatModelDropped = 912;

    /// <summary>A chat model's files are on disk and verified.</summary>
    public const int ChatModelProvisioned = 913;

    /// <summary>The memory budget allowed the requested context.</summary>
    public const int BudgetResolved = 920;

    /// <summary>The memory budget allowed a shorter context than the one requested.</summary>
    public const int BudgetReduced = 921;

    /// <summary>The memory budget refused.</summary>
    public const int BudgetRefused = 922;

    /// <summary>The platform reported nothing usable and the memory gate was skipped.</summary>
    public const int BudgetUnknown = 923;

    /// <summary>A turn began decoding.</summary>
    public const int TurnStarted = 930;

    /// <summary>A turn finished.</summary>
    public const int TurnCompleted = 931;

    /// <summary>A turn is waiting for the model gate.</summary>
    public const int TurnQueued = 932;

    /// <summary>A turn was refused.</summary>
    public const int TurnRejected = 933;

    /// <summary>A turn was cut short by the OS, memory pressure or thermal state.</summary>
    public const int TurnTerminated = 934;

    /// <summary>History reduction evicted at least one message.</summary>
    public const int HistoryReduced = 935;

    /// <summary>The decode loop is pacing itself because of thermal pressure.</summary>
    public const int ThermalThrottled = 936;

    /// <summary>A turn was aborted because of thermal pressure.</summary>
    public const int ThermalAborted = 937;

    /// <summary>The model's chat template was probed at warm-up.</summary>
    public const int ChatTemplateProbed = 950;

    /// <summary>minja could not parse the model's chat template.</summary>
    public const int ChatTemplateUnsupported = 951;

    /// <summary>A consumer-supplied <see cref="ChatPromptFormatter"/> replaced the model's template.</summary>
    public const int PromptFormatterOverridden = 952;

    /// <summary>Constrained decoding was probed.</summary>
    public const int GuidanceProbed = 953;
}

/// <summary>
/// The <c>AdditionalProperties</c> keys sub-project 4 writes. Public so a consumer never types a
/// string literal and an OpenTelemetry exporter can allow-list them.
/// </summary>
public static class EdgeChatProperties
{
    /// <summary>A <see cref="ChatTurnStatus"/>. One key, one strongly-typed record.</summary>
    public const string TurnStatus = "qavren.edge.chat.turn";

    /// <summary>
    /// A <c>IReadOnlyList&lt;string&gt;</c> of <c>ChatOptions</c> members this client did not
    /// honour on the request. Present only when non-empty.
    /// </summary>
    public const string UnhonouredOptions = "qavren.edge.chat.unhonouredOptions";
}
