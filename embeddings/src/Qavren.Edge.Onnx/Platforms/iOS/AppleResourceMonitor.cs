using System.Runtime.InteropServices;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx.Internal;

namespace Qavren.Edge.Onnx;

/// <summary>
/// Reads <c>os_proc_available_memory()</c>, <c>NSProcessInfo.ThermalState</c> and
/// <c>NSProcessInfo.LowPowerModeEnabled</c>.
/// </summary>
/// <remarks>
/// <c>os_proc_available_memory()</c> returning 0 means "unknown, or already over the limit". It is
/// treated as UNKNOWN and never as a refusal: the session host's memory pre-flight is required to
/// skip its check rather than fail it when the platform says nothing usable.
/// </remarks>
internal sealed class AppleResourceMonitor(TimeProvider? timeProvider = null)
    : EdgeResourceMonitorBase(timeProvider)
{
    // DllImport rather than LibraryImport on purpose: the source generator emits unsafe code, and
    // turning AllowUnsafeBlocks on for the whole assembly to reach one argument-less entry point
    // that returns a size_t is a bad trade. There is nothing to marshal here, so the two are
    // identical at runtime and both are AOT-safe.
#pragma warning disable SYSLIB1054
    [DllImport("__Internal", EntryPoint = "os_proc_available_memory")]
    private static extern nint OsProcAvailableMemory();
#pragma warning restore SYSLIB1054

    /// <inheritdoc />
    protected override EdgeResourceSnapshot Capture(EdgeMemoryPressure? lastPressure)
    {
        long? available = null;
        try
        {
            var bytes = (long)OsProcAvailableMemory();
            if (bytes > 0)
            {
                available = bytes;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Older or trimmed runtimes: unknown, not zero.
        }

        var processInfo = NSProcessInfo.ProcessInfo;

        var thermal = processInfo.ThermalState switch
        {
            NSProcessInfoThermalState.Nominal => EdgeThermalState.Nominal,
            NSProcessInfoThermalState.Fair => EdgeThermalState.Fair,
            NSProcessInfoThermalState.Serious => EdgeThermalState.Serious,
            NSProcessInfoThermalState.Critical => EdgeThermalState.Critical,
            _ => EdgeThermalState.Unknown,
        };

        return new EdgeResourceSnapshot(
            available,
            IsLowMemory: null,
            thermal,
            ThermalHeadroom: null,
            processInfo.LowPowerModeEnabled,
            lastPressure);
    }
}
