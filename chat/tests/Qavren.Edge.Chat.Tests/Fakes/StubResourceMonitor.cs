using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// One scripted <see cref="EdgeResourceSnapshot"/>, handed to the memory gate. The gate is pure -
/// no ORT, no GenAI, no I/O - so every assertion in <c>ChatMemoryBudgetTests</c> runs against this
/// and nothing native.
/// </summary>
internal sealed class StubResourceMonitor : IEdgeResourceMonitor
{
    private EdgeResourceSnapshot _snapshot;

    public StubResourceMonitor(
        long? availableMemoryBytes,
        EdgeThermalState thermal = EdgeThermalState.Nominal,
        bool? isLowMemory = null,
        float? thermalHeadroom = null,
        bool? isLowPowerMode = null)
        => _snapshot = new EdgeResourceSnapshot(
            availableMemoryBytes,
            isLowMemory,
            thermal,
            thermalHeadroom,
            isLowPowerMode,
            LastPressure: null);

    public EdgeMemoryPressure? LastPressure { get; private set; }

    public int Reads { get; private set; }

    public EdgeResourceSnapshot Read()
    {
        Reads++;
        return _snapshot with { LastPressure = LastPressure };
    }

    public void SetPressure(EdgeMemoryPressure? level) => LastPressure = level;

    public void SetSnapshot(EdgeResourceSnapshot snapshot) => _snapshot = snapshot;
}
