using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Ingestion.Onnx;

/// <summary>
/// <see cref="IIngestionThrottle"/> over SP2's <see cref="IEdgeResourceMonitor.Read"/> (spec 10.2).
/// The table below is evaluated top to bottom, <b>first match wins</b>, and every halving is
/// clamped to <see cref="ResourceMonitorThrottleOptions.MinBatchSize"/>:
/// <list type="table">
/// <item><term><c>LastPressure == Critical</c></term><description>pause, <c>memory:critical</c></description></item>
/// <item><term><c>AvailableMemoryBytes &lt; 48 MiB</c></term><description>pause, <c>memory:floor</c></description></item>
/// <item><term><c>Thermal == Critical</c></term><description>pause, <c>thermal:critical</c></description></item>
/// <item><term><c>Thermal == Serious</c></term><description>batch / 2, delay 250 ms, <c>thermal:serious</c></description></item>
/// <item><term><c>LastPressure == Moderate</c></term><description>batch / 2, <c>memory:moderate</c></description></item>
/// <item><term><c>AvailableMemoryBytes &lt; 96 MiB</c></term><description>batch / 2, <c>memory:warn</c></description></item>
/// <item><term><c>IsLowPowerMode == true</c></term><description>batch = min(batch, 4), delay 100 ms, <c>power:low</c></description></item>
/// <item><term>otherwise</term><description>proceed at the configured batch</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <b><see cref="EdgeThermalState.Unknown"/> and a null memory reading are NO SIGNAL, which is
/// proceed - never "fine" and never a pause.</b> That mirrors <see cref="EdgeResourceSnapshot"/>'s
/// own contract, where every nullable member means unknown. The batch is derived from
/// <see cref="IngestionThrottleContext.ConfiguredBatchSize"/>, not from the current one: the
/// runner already applies the lifecycle observer's shrink and takes the smaller of the two, so a
/// throttle that halved the already-halved value would collapse to the floor on the second call.
/// <see cref="Evaluate"/> never blocks; <see cref="IEdgeResourceMonitor.Read"/> is a one-second
/// cached read.
/// </remarks>
public sealed class ResourceMonitorThrottle : IIngestionThrottle
{
    private const long MemoryFloorBytes = 48L * 1024 * 1024;
    private const long MemoryWarnBytes = 96L * 1024 * 1024;
    private const int LowPowerBatch = 4;
    private static readonly TimeSpan ThermalSeriousDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LowPowerDelay = TimeSpan.FromMilliseconds(100);

    private readonly IEdgeResourceMonitor _monitor;
    private readonly ResourceMonitorThrottleOptions _options;

    /// <summary>Creates the throttle.</summary>
    /// <param name="monitor">SP2's resource monitor.</param>
    /// <param name="options">The knobs, or null for the defaults.</param>
    public ResourceMonitorThrottle(IEdgeResourceMonitor monitor, ResourceMonitorThrottleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        _options = options ?? new ResourceMonitorThrottleOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MinBatchSize, 1);

        _monitor = monitor;
    }

    /// <inheritdoc />
    public string Name => "resource-monitor";

    /// <inheritdoc />
    public IngestionThrottleDecision Evaluate(IngestionThrottleContext context)
    {
        var snapshot = _monitor.Read();
        var configured = Math.Max(1, context.ConfiguredBatchSize);

        if (snapshot.LastPressure == EdgeMemoryPressure.Critical)
        {
            return Pause(configured, "memory:critical");
        }

        if (snapshot.AvailableMemoryBytes is { } floorReading && floorReading < MemoryFloorBytes)
        {
            return Pause(configured, "memory:floor");
        }

        if (!_options.IgnoreThermalState)
        {
            if (snapshot.Thermal == EdgeThermalState.Critical)
            {
                return Pause(configured, "thermal:critical");
            }

            if (snapshot.Thermal == EdgeThermalState.Serious)
            {
                return Adjust(configured, configured / 2, ThermalSeriousDelay, "thermal:serious");
            }
        }

        if (snapshot.LastPressure == EdgeMemoryPressure.Moderate)
        {
            return Adjust(configured, configured / 2, TimeSpan.Zero, "memory:moderate");
        }

        if (snapshot.AvailableMemoryBytes is { } warnReading && warnReading < MemoryWarnBytes)
        {
            return Adjust(configured, configured / 2, TimeSpan.Zero, "memory:warn");
        }

        if (!_options.IgnoreLowPowerMode && snapshot.IsLowPowerMode == true)
        {
            return Adjust(configured, Math.Min(configured, LowPowerBatch), LowPowerDelay, "power:low");
        }

        return IngestionThrottleDecision.Proceed(configured);
    }

    private static IngestionThrottleDecision Pause(int configured, string reason) =>
        new(configured, TimeSpan.Zero, Pause: true, reason);

    private IngestionThrottleDecision Adjust(int configured, int batch, TimeSpan delay, string reason) =>
        new(Clamp(configured, batch), delay, Pause: false, reason);

    /// <summary>Never below the floor, never above what was configured.</summary>
    private int Clamp(int configured, int batch) =>
        Math.Min(configured, Math.Max(_options.MinBatchSize, batch));
}
