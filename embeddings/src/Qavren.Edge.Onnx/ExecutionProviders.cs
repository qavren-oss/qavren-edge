using Microsoft.ML.OnnxRuntime;

namespace Qavren.Edge.Onnx;

/// <summary>
/// The providers SP2 can actually append. NNAPI is deliberately absent - see spec 9.2 and ADR
/// 0004: ORT's portable <c>AppendExecutionProvider(string, Dictionary&lt;string,string&gt;)</c>
/// overload documents support for "QNN", "SNPE", "XNNPACK", "CoreML" and "AZURE" only, so NNAPI is
/// not reachable through the one code path this package uses. The typed helpers are never called:
/// <c>AppendExecutionProvider_CoreML</c> calls a native entry point deprecated in ORT 1.20.0, and
/// <c>AppendExecutionProvider_Nnapi</c> is compiled inside <c>#if __ANDROID__</c> and throws on
/// every other build.
/// </summary>
public enum EdgeExecutionProvider
{
    /// <summary>ORT's always-present CPU provider. Never appended explicitly; it is the fallback.</summary>
    Cpu = 0,

    /// <summary>XNNPACK, the Android accelerator, compiled into the shipped AAR.</summary>
    XnnPack = 1,

    /// <summary>CoreML, on iOS, the iOS simulator and Mac Catalyst.</summary>
    CoreMl = 2,
}

/// <summary>
/// One attempt. <see cref="Accepted"/> means <c>AppendExecutionProvider</c> returned without
/// throwing - it does NOT mean this provider executed every node. ORT exposes no managed API for
/// per-node EP assignment; <see cref="CoreMlProviderOptions.ProfileComputePlan"/> is the only
/// truthful signal for that.
/// </summary>
/// <param name="Provider">The provider that was tried.</param>
/// <param name="Accepted">
/// Whether <c>AppendExecutionProvider</c> returned without throwing. It does <b>not</b> mean this
/// provider executed every node, or any node.
/// </param>
/// <param name="Options">The option dictionary that was handed to ORT.</param>
/// <param name="Failure">The exception message, when the append threw.</param>
public sealed record ExecutionProviderAttempt(
    EdgeExecutionProvider Provider,
    bool Accepted,
    IReadOnlyDictionary<string, string> Options,
    string? Failure);

/// <summary>The outcome of applying an <see cref="OnnxExecutionProviderPolicy"/> to one session.</summary>
/// <param name="Accepted">
/// The first provider in the policy order that appended without throwing. See
/// <see cref="ExecutionProviderAttempt.Accepted"/> for what that does and does not prove.
/// </param>
/// <param name="Attempts">Every attempt, in the order they were made.</param>
public sealed record ExecutionProviderReport(
    EdgeExecutionProvider Accepted,
    IReadOnlyList<ExecutionProviderAttempt> Attempts);

/// <summary>CoreML execution-provider options, as ORT's string-keyed provider dictionary spells them.</summary>
public sealed class CoreMlProviderOptions
{
    /// <summary>
    /// MLProgram, not ORT's NeuralNetwork default: NeuralNetwork's supported-op table lacks
    /// <c>LayerNormalization</c>, <c>Gelu</c> and <c>Erf</c>, so a BERT encoder fragments into
    /// CPU-fallback partitions and pays a CPU-to-ANE round trip per block.
    /// </summary>
    public string ModelFormat { get; set; } = "MLProgram";

    /// <summary>Which hardware CoreML may use. Default <c>CPUAndNeuralEngine</c>.</summary>
    public string MLComputeUnits { get; set; } = "CPUAndNeuralEngine";

    /// <summary>
    /// Emits <c>RequireStaticInputShapes=1</c>. <b>Default false, and that is load-bearing.</b>
    /// CoreML partitions the graph at SESSION-CREATION time from the graph's DECLARED shapes, not
    /// from the shapes fed at Run time. All four presets declare
    /// <c>input_ids</c>/<c>attention_mask</c>/<c>token_type_ids</c> as
    /// <c>[batch_size, sequence_length]</c> - symbolic. Setting this to 1 against symbolic dims
    /// means CoreML takes few or no nodes and the whole graph silently falls back to CPU, which
    /// <see cref="ExecutionProviderAttempt.Accepted"/> cannot detect. Turning it on is therefore
    /// only correct together with <see cref="OnnxSessionOptions.FreeDimensionOverrides"/> (or
    /// <c>OnnxEmbeddingOptions.PinnedSequenceLength</c>, which sets both), and the session factory
    /// refuses the combination without them.
    /// </summary>
    public bool RequireStaticInputShapes { get; set; }

    /// <summary>
    /// Emits <c>ModelCacheDirectory</c>. Unset, CoreML recompiles the captured subgraph on every
    /// session creation and leaks the artefact into the iOS tmp directory. The cache key is made
    /// content-addressed by the path itself: <c>&lt;OrtCache&gt;/&lt;modelId&gt;/&lt;sha16&gt;</c>.
    /// </summary>
    public bool EnableModelCache { get; set; } = true;

    /// <summary>iOS 18+. Emits <c>SpecializationStrategy=FastPrediction</c>.</summary>
    public bool FastPrediction { get; set; }

    /// <summary>Diagnostics only: logs the hardware each operator was dispatched to.</summary>
    public bool ProfileComputePlan { get; set; }
}

/// <summary>XNNPACK execution-provider options.</summary>
public sealed class XnnPackProviderOptions
{
    /// <summary>
    /// XNNPACK's own pool. Default <c>Math.Clamp(ProcessorCount / 2, 1, 4)</c>: saturating every
    /// core of a big.LITTLE phone is a thermal-throttle generator, not a speedup.
    /// </summary>
    public int? IntraOpNumThreads { get; set; }
}

/// <summary>How a session picks and configures its execution providers.</summary>
public sealed class OnnxExecutionProviderPolicy
{
    /// <summary>
    /// Tried in order; the first that appends without throwing wins. CPU is appended last
    /// whenever <see cref="FallBackToCpu"/> is true. Null uses the per-RID default in spec 9.2.
    /// </summary>
    public IReadOnlyList<EdgeExecutionProvider>? Order { get; set; }

    /// <summary>Whether CPU is appended last as the universal fallback. Default true.</summary>
    public bool FallBackToCpu { get; set; } = true;

    /// <summary>Failure of a required provider throws instead of falling through.</summary>
    public IReadOnlyList<EdgeExecutionProvider> Required { get; set; } = [];

    /// <summary>CoreML's options.</summary>
    public CoreMlProviderOptions CoreMl { get; } = new();

    /// <summary>XNNPACK's options.</summary>
    public XnnPackProviderOptions XnnPack { get; } = new();

    /// <summary>
    /// 0 lets ORT choose. Forced to 1 whenever XNNPACK is accepted, per ORT's own
    /// anti-contention guidance.
    /// </summary>
    public int IntraOpNumThreads { get; set; }

    /// <summary>The graph-optimization level handed to ORT. Default <c>ORT_ENABLE_ALL</c>.</summary>
    public GraphOptimizationLevel GraphOptimization { get; set; } = GraphOptimizationLevel.ORT_ENABLE_ALL;

    /// <summary>
    /// Merged over the computed options, keyed by ORT's provider name ("CoreML", "XNNPACK").
    /// Any other key is rejected before a single provider is appended - in particular "NNAPI",
    /// which this package does not offer at all (ADR 0004).
    /// </summary>
    public IDictionary<string, IReadOnlyDictionary<string, string>> Overrides { get; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
}
