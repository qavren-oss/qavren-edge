namespace Qavren.Edge.Embeddings.Tests.Tier3;

/// <summary>
/// The <c>SkipUnless</c> gate. Evaluated at RUNTIME, after the class constructor and after every
/// <c>BeforeAfterTestAttribute</c> - which is exactly why nothing in the test class's constructor
/// may touch a model. A "skipped" tier-3 test that loaded 23 MB first would cost every device lane
/// and every PR leg the download this tier exists to keep off them.
/// </summary>
/// <remarks>
/// This property lives on its own type rather than on <c>RealModelFacts</c>, so every fact names it
/// with <c>SkipType = typeof(ModelAvailable)</c>. xunit resolves <c>SkipUnless</c> against the test
/// class unless <c>SkipType</c> says otherwise, and a bare property name here would send it looking
/// for <c>RealModelFacts.Yes</c> and fail the test rather than skip it.
/// </remarks>
public static class ModelAvailable
{
    /// <summary>Set only by ci.yml's nightly <c>model-tests</c> job, and by a developer opting in.</summary>
    public static bool Yes =>
        Environment.GetEnvironmentVariable("QAVREN_EDGE_MODEL_DIR") is { Length: > 0 } dir
        && File.Exists(Path.Combine(dir, "onnx", "model_qint8_arm64.onnx"))
        && File.Exists(Path.Combine(dir, "vocab.txt"));
}
