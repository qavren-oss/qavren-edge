namespace Qavren.Edge.Ingestion.Onnx;

/// <summary>
/// What <see cref="ResourceMonitorThrottle"/> can be told to ignore, and how far it may shrink a
/// batch (spec 10.2). Every default here changes behaviour.
/// </summary>
public sealed class ResourceMonitorThrottleOptions
{
    /// <summary>
    /// The floor every halving is clamped to. 4, matching the floor the core's lifecycle observer
    /// applies to its own memory-pressure shrink, so the two never disagree about the smallest
    /// write window.
    /// </summary>
    public int MinBatchSize { get; set; } = 4;

    /// <summary>
    /// Skip the <c>power:low</c> row. Off: Low Power Mode is a user's explicit request to do less,
    /// and an app that disagrees says so here rather than by replacing the throttle.
    /// </summary>
    public bool IgnoreLowPowerMode { get; set; }

    /// <summary>
    /// Skip both thermal rows (<c>thermal:critical</c> and <c>thermal:serious</c>). Off. Memory
    /// rows are never skipped: a memory pause is what keeps the process alive.
    /// </summary>
    public bool IgnoreThermalState { get; set; }
}
