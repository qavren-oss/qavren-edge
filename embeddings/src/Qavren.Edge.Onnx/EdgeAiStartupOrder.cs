namespace Qavren.Edge.Onnx;

/// <summary>
/// The order registry for every SP2 startup task. The constants are published from L0 because
/// that is the lowest package all three share; the task that USES each order is registered by the
/// package named in the comment, and spec 14.1 is the authority on which is which.
/// </summary>
/// <remarks>
/// SP1's <c>EdgeStartupOrder</c> is NOT changed: 200/210/220/300 already fit between
/// <c>Migrations = 100</c> and <c>ConsumerDefault = 1000</c>.
/// </remarks>
public static class EdgeAiStartupOrder
{
    /// <summary>
    /// <c>OrtEnv.CreateInstanceWithOptions</c>. MUST precede any <c>SessionOptions</c>
    /// construction: ORT creates the environment implicitly on the first <c>SessionOptions</c>,
    /// after which <c>CreateInstanceWithOptions</c> throws. Registered by
    /// <c>Qavren.Edge.Onnx</c>.
    /// </summary>
    public const int OnnxEnvironment = 200;

    /// <summary>Opt-in. Registered by <c>Qavren.Edge.Onnx</c>.</summary>
    public const int ModelProvisioning = 210;

    /// <summary>
    /// Opt-in. Two tasks share this order: <c>Qavren.Edge.Onnx</c>'s
    /// <c>WarmUpSessionAtStartup</c> (loads the session; needs no tokenizer) and
    /// <c>Qavren.Edge.Embeddings.Onnx</c>'s <c>WarmUpEmbeddingsAtStartup</c> (load plus one dummy
    /// batch; needs the tokenizer and the generator, both of which live in L1).
    /// </summary>
    public const int SessionWarmUp = 220;

    /// <summary>
    /// Opt-in; the migration path (order 100) is preferred. Registered by
    /// <c>Qavren.Edge.VectorData</c>.
    /// </summary>
    public const int VectorSchema = 300;
}
