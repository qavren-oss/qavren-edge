using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// The caching and latching half of every <see cref="IEdgeResourceMonitor"/>. Reads are cached for
/// one second because Android's <c>GetThermalHeadroom</c> is rate-limited to ~1 Hz and returns
/// <c>NaN</c> when polled faster; the window is driven by the injected <see cref="TimeProvider"/>
/// so a test can assert it rather than sleep through it.
/// </summary>
internal abstract class EdgeResourceMonitorBase : IEdgeResourceMonitor
{
    /// <summary>How long one reading stands before the device is asked again.</summary>
    protected static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private EdgeResourceSnapshot? _cached;
    private DateTimeOffset _cachedAt;
    private EdgeMemoryPressure? _lastPressure;

    /// <summary>Creates the monitor.</summary>
    /// <param name="timeProvider">Drives the one-second cache window; null uses the system clock.</param>
    protected EdgeResourceMonitorBase(TimeProvider? timeProvider)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public EdgeMemoryPressure? LastPressure
    {
        get
        {
            lock (_gate)
            {
                return _lastPressure;
            }
        }
    }

    /// <inheritdoc />
    public EdgeResourceSnapshot Read()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_cached is not null && now - _cachedAt < CacheWindow)
            {
                return _cached;
            }

            _cached = Capture(_lastPressure);
            _cachedAt = now;
            return _cached;
        }
    }

    /// <inheritdoc />
    public void SetPressure(EdgeMemoryPressure? level)
    {
        lock (_gate)
        {
            _lastPressure = level;

            // The latch is part of the snapshot record, so a stale cached snapshot would report a
            // pressure level that has already changed. Invalidating is cheaper than reasoning about it.
            _cached = null;
        }
    }

    /// <summary>Asks the platform. Called at most once per <see cref="CacheWindow"/>.</summary>
    /// <param name="lastPressure">The current latch value, to be copied into the snapshot.</param>
    /// <returns>The fresh snapshot.</returns>
    protected abstract EdgeResourceSnapshot Capture(EdgeMemoryPressure? lastPressure);
}

/// <summary>
/// net10.0 and Windows. Reads <c>GC.GetGCMemoryInfo()</c> and reports
/// <see cref="EdgeThermalState.Unknown"/>: there is no cross-platform desktop thermal API, and
/// <see cref="EdgeThermalState.Unknown"/> means unknown, never "fine". Advisory only.
/// </summary>
internal sealed class DefaultEdgeResourceMonitor(TimeProvider? timeProvider = null)
    : EdgeResourceMonitorBase(timeProvider)
{
    /// <inheritdoc />
    protected override EdgeResourceSnapshot Capture(EdgeMemoryPressure? lastPressure)
    {
        var info = GC.GetGCMemoryInfo();

        long? available = null;
        bool? isLowMemory = null;

        if (info.TotalAvailableMemoryBytes > 0)
        {
            available = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        }

        if (info.HighMemoryLoadThresholdBytes > 0)
        {
            isLowMemory = info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes;
        }

        return new EdgeResourceSnapshot(
            available,
            isLowMemory,
            EdgeThermalState.Unknown,
            ThermalHeadroom: null,
            IsLowPowerMode: null,
            lastPressure);
    }
}
