using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Onnx;

/// <summary>The device's thermal state, as the platform reports it.</summary>
public enum EdgeThermalState
{
    /// <summary>The platform reports nothing usable. Never treat this as "fine".</summary>
    Unknown = 0,

    /// <summary>No thermal pressure.</summary>
    Nominal,

    /// <summary>Mild thermal pressure.</summary>
    Fair,

    /// <summary>Serious thermal pressure; the OS is already throttling.</summary>
    Serious,

    /// <summary>Critical thermal pressure.</summary>
    Critical,
}

/// <summary>
/// One reading. <b>Every nullable member means UNKNOWN, never "no".</b>
/// <see cref="AvailableMemoryBytes"/> null on a desktop where <c>GC.GetGCMemoryInfo()</c> reports
/// nothing usable is not a refusal, and the session host's memory pre-flight is required to skip
/// its check rather than fail it.
/// </summary>
/// <param name="AvailableMemoryBytes">Free memory the process may still take, or null if unknown.</param>
/// <param name="IsLowMemory">Whether the OS considers itself low on memory, or null if unknown.</param>
/// <param name="Thermal">The thermal state; <see cref="EdgeThermalState.Unknown"/> on desktop.</param>
/// <param name="ThermalHeadroom">Android's <c>GetThermalHeadroom(10)</c>, or null.</param>
/// <param name="IsLowPowerMode">Apple's low-power-mode flag, or null.</param>
/// <param name="LastPressure">The latched value of <see cref="IEdgeResourceMonitor.LastPressure"/>.</param>
public sealed record EdgeResourceSnapshot(
    long? AvailableMemoryBytes,
    bool? IsLowMemory,
    EdgeThermalState Thermal,
    float? ThermalHeadroom,
    bool? IsLowPowerMode,
    EdgeMemoryPressure? LastPressure);

/// <summary>
/// Apple: <c>os_proc_available_memory()</c> (0 means unknown or already over - treated as unknown,
/// never as a refusal) and <c>ProcessInfo.ThermalState</c>/<c>IsLowPowerModeEnabled</c>.
/// Android: <c>ActivityManager.GetMemoryInfo</c> (availMem, lowMemory),
/// <c>PowerManager.CurrentThermalStatus</c> and <c>GetThermalHeadroom(10)</c>.
/// Desktop: <c>GC.GetGCMemoryInfo()</c>, advisory only.
/// Reads are cached for one second: Android's <c>GetThermalHeadroom</c> is rate-limited to ~1 Hz
/// and returns <c>NaN</c> when polled faster.
/// </summary>
public interface IEdgeResourceMonitor
{
    /// <summary>Reads the device. Cached for one second, driven by the injected TimeProvider.</summary>
    /// <returns>The current snapshot.</returns>
    EdgeResourceSnapshot Read();

    /// <summary>
    /// The most recent level SP1's lifecycle hub reported, latched here by
    /// <c>OnnxLifecycleObserver</c> (spec 14.2) and cleared to <see langword="null"/> on
    /// <c>Resumed</c>.
    /// <para>
    /// <b>Nullable, and that is load-bearing.</b> SP1's merged <see cref="EdgeMemoryPressure"/> is
    /// <c>{ Low, Moderate, Critical }</c> with no <c>None</c> member and <c>Low</c> as the zero
    /// value, so a non-nullable latch could not express "nothing has been reported" or "cleared" -
    /// it would read as <c>Low</c> from the moment the container is built. Adding a
    /// <c>None = 0</c> member would renumber <c>Low</c>/<c>Moderate</c>/<c>Critical</c> and is
    /// therefore not an additive change, which spec 5 forbids. <see langword="null"/> costs
    /// nothing and matches SP1's own idiom: <c>EdgeLifecycleRecord.Level</c> is already
    /// <c>EdgeMemoryPressure?</c>.
    /// </para>
    /// This is the shared pressure signal <c>Qavren.Edge.Embeddings.Onnx</c> reads to shrink its
    /// batch size: the batch size lives in the L1 package and the lifecycle observer lives in L0,
    /// so the flag has to sit in L0 or the dependency direction in spec 4.1 breaks. It is a latch,
    /// not an event stream - a reader gets the current level and nothing else.
    /// </summary>
    EdgeMemoryPressure? LastPressure { get; }

    /// <summary>
    /// Called by <c>OnnxLifecycleObserver</c> only. Not part of the consumer surface.
    /// <see langword="null"/> clears the latch.
    /// </summary>
    /// <param name="level">The level to latch, or null to clear.</param>
    void SetPressure(EdgeMemoryPressure? level);
}
