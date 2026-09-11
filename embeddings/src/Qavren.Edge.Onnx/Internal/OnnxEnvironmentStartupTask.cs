using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// What the startup task learned, read back by <see cref="OnnxDiagnosticsContributor"/>. A
/// separate singleton rather than a property on the task itself, so the contributor and the task
/// can be resolved independently and still agree.
/// </summary>
internal sealed class OnnxEnvironmentState
{
    /// <summary>Another library created <c>OrtEnv</c> before we got there.</summary>
    public bool EnvironmentPreexisting { get; set; }

    /// <summary>This library created <c>OrtEnv</c>.</summary>
    public bool EnvironmentCreated { get; set; }
}

/// <summary>
/// Startup order 200. Must run before anything can construct a <c>SessionOptions</c>: ORT creates
/// the environment implicitly on the first <c>SessionOptions</c>, and
/// <c>CreateInstanceWithOptions</c> then throws "OrtEnv singleton instance already exists".
/// </summary>
internal sealed class OnnxEnvironmentStartupTask(
    IOptions<OnnxOptions> options,
    OnnxEnvironmentState state,
    ILogger<OnnxEnvironmentStartupTask> logger) : IEdgeStartupTask
{
    // Held in a static field so the native side's function pointer is never collected. ORT's
    // environment is process-wide and one-shot, so a static root is the correct lifetime.
    private static DOrtLoggingFunction? s_loggingBridge;
    private static ILogger? s_bridgeLogger;

    private static readonly Action<ILogger, string, string, Exception?> s_environmentCreated =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.OrtEnvironmentCreated, nameof(EdgeAiEventIds.OrtEnvironmentCreated)),
            "ONNX Runtime environment created with log id {LogId} at severity {Severity}.");

    // EdgeErrorCode.OnnxEnvironmentAlreadyCreated is carried as a log field, not thrown: 5001 is
    // the one SP2 code whose contract (errors.md 5001) is "informational only - never thrown", so
    // its only raise site is this message.
    private static readonly Action<ILogger, EdgeErrorCode, Exception?> s_environmentPreexisting =
        LoggerMessage.Define<EdgeErrorCode>(
            LogLevel.Warning,
            new EventId(EdgeAiEventIds.OrtEnvironmentPreexisting, nameof(EdgeAiEventIds.OrtEnvironmentPreexisting)),
            "[{ErrorCode}] The ONNX Runtime environment already existed, so Qavren.Edge.Onnx's log id, " +
            "severity and logging bridge were not applied. Another library in this process created it " +
            "first; that is not a failure and startup continues.");

    // One delegate per ILogger level, because LoggerMessage.Define bakes the level in and ORT's
    // severity is only known at callback time.
    private static readonly Action<ILogger, string, string, string, Exception?> s_ortLogTrace = OrtLog(LogLevel.Trace);
    private static readonly Action<ILogger, string, string, string, Exception?> s_ortLogInformation = OrtLog(LogLevel.Information);
    private static readonly Action<ILogger, string, string, string, Exception?> s_ortLogWarning = OrtLog(LogLevel.Warning);
    private static readonly Action<ILogger, string, string, string, Exception?> s_ortLogError = OrtLog(LogLevel.Error);
    private static readonly Action<ILogger, string, string, string, Exception?> s_ortLogCritical = OrtLog(LogLevel.Critical);

    private static Action<ILogger, string, string, string, Exception?> OrtLog(LogLevel level) =>
        LoggerMessage.Define<string, string, string>(
            level,
            new EventId(EdgeAiEventIds.OrtLog, nameof(EdgeAiEventIds.OrtLog)),
            "[ort:{Category}] {Message} ({CodeLocation})");

    /// <inheritdoc />
    public int Order => EdgeAiStartupOrder.OnnxEnvironment;

    /// <inheritdoc />
    public Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = options.Value;

        // FIRST statement that touches ORT, by contract: the resolver is registered by the static
        // constructor of NativeMethods, which any other ORT member access would trigger.
        if (value.DisableOrtDllImportResolver)
        {
            OrtEnv.DisableDllImportResolver = true;
        }

        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        if (!ExecutionProviderPolicyResolver.IsRuntimeSupported(runtimeIdentifier))
        {
            // A named error at startup rather than a DllNotFoundException on first embed.
            throw ExecutionProviderPolicyResolver.UnsupportedRuntime(runtimeIdentifier);
        }

        var (creationOptions, _) = BuildCreationOptions();

        if (OrtEnv.IsCreated)
        {
            state.EnvironmentPreexisting = true;
            s_environmentPreexisting(logger, EdgeErrorCode.OnnxEnvironmentAlreadyCreated, null);
            return Task.CompletedTask;
        }

        try
        {
            OrtEnv.CreateInstanceWithOptions(ref creationOptions);
        }
        catch (OnnxRuntimeException ex)
        {
            // Lost the race between IsCreated and the call. Another library winning is not our
            // failure to crash on.
            state.EnvironmentPreexisting = true;
            s_environmentPreexisting(logger, EdgeErrorCode.OnnxEnvironmentAlreadyCreated, ex);
            return Task.CompletedTask;
        }

        state.EnvironmentCreated = true;
        s_environmentCreated(logger, value.LogId, value.LogSeverity.ToString(), null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds exactly the <see cref="EnvironmentCreationOptions"/> <see cref="RunAsync"/> would
    /// pass to ORT, and installs the logging bridge when one is asked for. Split out so the
    /// defaults can be asserted without creating a real <c>OrtEnv</c> - a process-wide singleton
    /// whose second creation throws, which would make the assertion order-dependent.
    /// </summary>
    /// <returns>The creation options and whether a logging bridge was supplied.</returns>
    public (EnvironmentCreationOptions Options, bool HasLoggingBridge) BuildCreationOptions()
    {
        var value = options.Value;

        DOrtLoggingFunction? bridge = null;
        if (value.BridgeNativeLogging)
        {
            s_bridgeLogger = logger;
            s_loggingBridge ??= ForwardOrtLog;
            bridge = s_loggingBridge;
        }

        var creationOptions = new EnvironmentCreationOptions
        {
            logId = value.LogId,
            logLevel = value.LogSeverity,
            loggingFunction = bridge,
        };

        return (creationOptions, bridge is not null);
    }

    private static void ForwardOrtLog(
        IntPtr param,
        OrtLoggingLevel severity,
        string category,
        string logId,
        string codeLocation,
        string message)
    {
        var sink = s_bridgeLogger;
        if (sink is null)
        {
            return;
        }

        // ORT's own severity floor already filtered this line; the ILogger level is chosen to match
        // so a consumer's filter sees the severity ORT meant. The log id and the opaque callback
        // param are ours already - EnvironmentCreationOptions carried both - so neither is forwarded.
        _ = logId;
        _ = param;

        var write = severity switch
        {
            OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE => s_ortLogTrace,
            OrtLoggingLevel.ORT_LOGGING_LEVEL_INFO => s_ortLogInformation,
            OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING => s_ortLogWarning,
            OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR => s_ortLogError,
            _ => s_ortLogCritical,
        };

        write(sink, category, message, codeLocation, null);
    }
}
