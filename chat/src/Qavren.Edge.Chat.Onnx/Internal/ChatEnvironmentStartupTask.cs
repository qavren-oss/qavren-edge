using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// What the order-400 task learned, read back by the model host, the lifecycle observer and the
/// diagnostics contributor.
/// </summary>
/// <remarks>
/// <b>No member here is typed as an ORT GenAI type, and that is the rule rather than a style
/// choice.</b> This singleton is resolvable during composition, and spec section 8.1 forbids any
/// GenAI type appearing in a constructor parameter, a field initialiser or a static constructor of
/// anything DI builds eagerly - because GenAI creates ORT's process-wide <c>OrtEnv</c> from
/// <i>native</i> code, where sub-project 2's managed guard cannot see it, and the cost of losing
/// that race is sub-project 2's log id, severity and <c>DOrtLoggingFunction</c> bridge with no
/// error anywhere. The handle is therefore held as an <see cref="IDisposable"/>.
/// </remarks>
internal sealed class ChatEnvironmentState
{
    private IDisposable? _runtimeHandle;
    private int _shutdownCount;

    /// <summary>Whether the order-400 task has run.</summary>
    public bool Started { get; private set; }

    /// <summary>
    /// <c>OrtEnv.IsCreated</c> sampled at order 400, after sub-project 2's order-200 task and
    /// before sub-project 4 names its first GenAI type. Read it beside sub-project 2's
    /// <c>ortEnvironmentPreexisting</c>; spec section 8.1 has the table.
    /// </summary>
    public bool OrtEnvCreatedBeforeGenAi { get; private set; }

    /// <summary>Whether sub-project 4 created the process-wide <c>OgaHandle</c>.</summary>
    public bool OgaHandleOwned { get; private set; }

    /// <summary>Whether <c>Utils.DisableTelemetryEvents()</c> was called.</summary>
    public bool TelemetryDisabled { get; private set; }

    /// <summary>The device profile, read once and cached for the process.</summary>
    public EdgeChatDeviceProfile Profile { get; private set; }

    /// <summary>How many times the runtime handle has actually been disposed. Never above one.</summary>
    public int ShutdownCount => Volatile.Read(ref _shutdownCount);

    /// <summary>Records everything the order-400 task learned.</summary>
    /// <param name="profile">The cached device profile.</param>
    /// <param name="ortEnvCreatedBeforeGenAi">The <c>OrtEnv.IsCreated</c> sample.</param>
    /// <param name="runtimeHandle">The owned <c>OgaHandle</c>, or null when it is not ours.</param>
    /// <param name="telemetryDisabled">Whether telemetry was turned off.</param>
    public void MarkStarted(
        EdgeChatDeviceProfile profile,
        bool ortEnvCreatedBeforeGenAi,
        IDisposable? runtimeHandle,
        bool telemetryDisabled)
    {
        Profile = profile;
        OrtEnvCreatedBeforeGenAi = ortEnvCreatedBeforeGenAi;
        _runtimeHandle = runtimeHandle;
        OgaHandleOwned = runtimeHandle is not null;
        TelemetryDisabled = telemetryDisabled;
        Started = true;
    }

    /// <summary>
    /// Disposes the owned handle - which calls <c>OgaShutdown()</c> - exactly once, however many
    /// observers call it.
    /// </summary>
    /// <returns><see langword="true"/> when this call was the one that shut the runtime down.</returns>
    public bool ShutdownRuntime()
    {
        var handle = Interlocked.Exchange(ref _runtimeHandle, null);
        if (handle is null)
        {
            return false;
        }

        handle.Dispose();
        Interlocked.Increment(ref _shutdownCount);
        return true;
    }

    /// <summary>
    /// Throws <see cref="EdgeErrorCode.ChatEnvironmentNotStarted"/> (7001) when the order-400 task
    /// has not run.
    /// </summary>
    /// <exception cref="EdgeChatException">The environment task has not run.</exception>
    public void ThrowIfNotStarted()
    {
        if (Started)
        {
            return;
        }

        throw new EdgeChatException(
            EdgeErrorCode.ChatEnvironmentNotStarted,
            "The ORT GenAI environment has not been started, so no chat model can be loaded. " +
            "Sub-project 4's order-400 startup task is what creates the process-wide OgaHandle, " +
            "checks the RID and the Android ABI, and records whether OrtEnv already existed.")
        {
            Remediation =
                "Call AddOnnxChat() during composition and resolve IChatClient from the container " +
                "rather than constructing EdgeChatClient yourself.",
        };
    }
}

/// <summary>
/// Startup order 400. <b>The first code in this assembly permitted to name an ORT GenAI type</b>,
/// and steps 1-3 below still do not.
/// </summary>
/// <remarks>
/// Spec section 8.1's ordering rule, enforced: ORT GenAI creates ORT's process-wide <c>OrtEnv</c>
/// from native code on its first call, sub-project 2's <c>OrtEnv.IsCreated</c> guard is managed and
/// cannot see that, and GenAI going first therefore costs sub-project 2 its log id, its severity
/// and its logging bridge silently. Measured on this box: creating and disposing an
/// <c>OgaHandle</c> leaves <c>OrtEnv.IsCreated</c> <see langword="false"/>.
/// </remarks>
internal sealed class ChatEnvironmentStartupTask(
    IEdgeChatDeviceProfileProvider deviceProfiles,
    ChatEnvironmentState state,
    IOptions<EdgeGenAiOptions> options,
    ILogger<ChatEnvironmentStartupTask> logger) : IEdgeStartupTask
{
    private static readonly Action<ILogger, string, string?, Exception?> s_runtimeInitialized =
        LoggerMessage.Define<string, string?>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.GenAiRuntimeInitialized, nameof(EdgeChatEventIds.GenAiRuntimeInitialized)),
            "ORT GenAI runtime initialized on {RuntimeIdentifier} (abi {Abi}).");

    private static readonly Action<ILogger, Exception?> s_telemetryDisabled =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.GenAiTelemetryDisabled, nameof(EdgeChatEventIds.GenAiTelemetryDisabled)),
            "ORT GenAI telemetry events disabled.");

    private static readonly Action<ILogger, Exception?> s_environmentOutOfOrder =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(EdgeChatEventIds.GenAiEnvironmentOutOfOrder, nameof(EdgeChatEventIds.GenAiEnvironmentOutOfOrder)),
            "OrtEnv had not been created when Qavren.Edge.Chat.Onnx's order-400 task ran, so ORT " +
            "GenAI is about to create it from native code and Qavren.Edge.Onnx's log id, severity " +
            "and logging bridge will be discarded. The two likely causes are a GenAI type touched " +
            "during composition, and AddOnnx() not being registered at all.");

    /// <inheritdoc />
    public int Order => EdgeChatStartupOrder.ChatEnvironment;

    /// <inheritdoc />
    public Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. The device profile, read once and cached for the life of the process.
        var profile = deviceProfiles.Read();

        // 2. The LAST moment this answer is still meaningful. Reading OrtEnv.IsCreated does not
        //    create it; step 4 is what would.
        var ortEnvCreatedBeforeGenAi = OrtEnv.IsCreated;
        if (!ortEnvCreatedBeforeGenAi)
        {
            s_environmentOutOfOrder(logger, null);
        }

        // 3. A named error at launch rather than a DllNotFoundException on the user's first message.
        GenAiRuntimeSupport.ThrowIfUnsupported(profile);

        var value = options.Value;

        // 4. The first line in this assembly that names a GenAI type.
        IDisposable? handle = value.OwnRuntimeHandle
            ? new Microsoft.ML.OnnxRuntimeGenAI.OgaHandle()
            : null;

        // 5. Opt-OUT telemetry, turned off by default. The static call does not throw (measured).
        if (value.DisableTelemetry)
        {
            Microsoft.ML.OnnxRuntimeGenAI.Utils.DisableTelemetryEvents();
            s_telemetryDisabled(logger, null);
        }

        state.MarkStarted(profile, ortEnvCreatedBeforeGenAi, handle, value.DisableTelemetry);
        s_runtimeInitialized(logger, profile.RuntimeIdentifier, profile.Abi, null);

        return Task.CompletedTask;
    }
}
