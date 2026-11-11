using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Onnx.Tests.Stubs;

/// <summary>
/// An <see cref="IEdgeResourceMonitor"/> that reads nothing and remembers everything, so a test
/// about registration or about the lifecycle latch never depends on what this machine's GC happens
/// to report.
/// </summary>
internal sealed class StubResourceMonitor : IEdgeResourceMonitor
{
    public EdgeMemoryPressure? LastPressure { get; private set; }

    public int ReadCount { get; private set; }

    public EdgeResourceSnapshot Next { get; set; } =
        new(AvailableMemoryBytes: 1024L * 1024 * 1024,
            IsLowMemory: false,
            EdgeThermalState.Unknown,
            ThermalHeadroom: null,
            IsLowPowerMode: null,
            LastPressure: null);

    /// <summary>
    /// Called inside every <see cref="Read"/>. The session host's memory pre-flight reads once,
    /// after the load gate and before the native session is built - which is where a test puts
    /// something that has to happen "while the session loads".
    /// </summary>
    public Action? OnRead { get; set; }

    public EdgeResourceSnapshot Read()
    {
        ReadCount++;
        OnRead?.Invoke();
        return Next with { LastPressure = LastPressure };
    }

    public void SetPressure(EdgeMemoryPressure? level) => LastPressure = level;
}
