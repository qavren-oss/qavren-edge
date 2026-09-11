namespace Qavren.Edge.Onnx;

/// <summary>Per-model session settings. One instance per registered model.</summary>
public sealed class OnnxSessionOptions
{
    /// <summary>How this session picks and configures its execution providers.</summary>
    public OnnxExecutionProviderPolicy ExecutionProviders { get; } = new();

    /// <summary>Dispose this session on <c>MemoryPressure(Critical)</c> and reload lazily.</summary>
    public bool DropOnMemoryPressure { get; set; } = true;

    /// <summary>
    /// Refuse a session when the OS reports less than
    /// <c>modelBytes * factor + </c><see cref="MemoryHeadroomBytes"/>. 0 disables the pre-flight.
    /// The default is an engineering estimate anchored on the ~2x transient cost of session
    /// creation, NOT a measurement - see spec 19.
    /// </summary>
    public double MemoryHeadroomFactor { get; set; } = 2.5;

    /// <summary>The fixed part of the memory pre-flight budget. Default 48 MB.</summary>
    public long MemoryHeadroomBytes { get; set; } = 48L * 1024 * 1024;

    /// <summary>
    /// Symbolic dimension name -&gt; fixed value, applied with
    /// <c>SessionOptions.AddFreeDimensionOverrideByName</c> before the session is created. This is
    /// the ONLY mechanism that turns a graph's declared <c>[batch_size, sequence_length]</c> into
    /// static shapes, and therefore the only thing that makes
    /// <see cref="CoreMlProviderOptions.RequireStaticInputShapes"/> meaningful - padding the
    /// tensors fed at Run time does not, because CoreML partitions from the declared shapes at
    /// session creation. A session pinned this way serves exactly one shape: every batch is padded
    /// to it, and a second pinned shape would need a second session, which v1 does not do.
    /// Empty by default, so the default session is dynamic-shaped and CoreML partitions normally.
    /// <para>
    /// This is a DIFFERENT ORT API from <see cref="SessionConfigEntries"/>. The two are separate
    /// properties on purpose and are never merged.
    /// </para>
    /// </summary>
    public IDictionary<string, long> FreeDimensionOverrides { get; }
        = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>
    /// Escape hatch for <c>SessionOptions.AddSessionConfigEntry</c>. Unrelated to
    /// <see cref="FreeDimensionOverrides"/>, which is a different ORT API: that one rewrites the
    /// graph's declared symbolic dimensions, this one sets ORT's own string-keyed session
    /// configuration.
    /// </summary>
    public IDictionary<string, string> SessionConfigEntries { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
