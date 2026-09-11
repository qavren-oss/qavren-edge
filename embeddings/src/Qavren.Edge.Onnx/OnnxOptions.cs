using Microsoft.ML.OnnxRuntime;

namespace Qavren.Edge.Onnx;

/// <summary>
/// Process-wide ONNX Runtime settings, applied by the order-200 startup task.
/// </summary>
/// <remarks>
/// <see cref="LogSeverity"/> and <see cref="BridgeNativeLogging"/> default together, and the pair
/// is the reason a consumer sees ORT's native warnings in their own <c>ILogger</c> with no
/// configuration: <see cref="LogSeverity"/> is what the environment passes to
/// <c>EnvironmentCreationOptions</c>, and <see cref="BridgeNativeLogging"/> is what installs the
/// <c>DOrtLoggingFunction</c> that forwards them. Turning the bridge off leaves ORT logging to its
/// own stderr sink, which on a device is nowhere.
/// </remarks>
public sealed class OnnxOptions
{
    /// <summary>The severity floor handed to <c>EnvironmentCreationOptions.logLevel</c>.</summary>
    public OrtLoggingLevel LogSeverity { get; set; } = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

    /// <summary>The log id ORT stamps on its own lines.</summary>
    public string LogId { get; set; } = "qavren.edge";

    /// <summary>Bridges ORT's native logger into <c>ILogger</c> through <c>DOrtLoggingFunction</c>.</summary>
    public bool BridgeNativeLogging { get; set; } = true;

    /// <summary>
    /// Calls <c>SessionOptions.DisablePerSessionThreads()</c> so every session shares one process
    /// pool. Default true once more than one model is registered.
    /// </summary>
    public bool? ShareThreadPool { get; set; }

    /// <summary>
    /// Sets <c>OrtEnv.DisableDllImportResolver</c> before any ORT type is touched. Default false;
    /// the escape hatch if SP1's provider resolver ever collides with ORT's.
    /// </summary>
    public bool DisableOrtDllImportResolver { get; set; }
}
