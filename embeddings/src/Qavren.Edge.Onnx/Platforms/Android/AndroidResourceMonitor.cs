using Android.Content;
using Android.OS;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx.Internal;

namespace Qavren.Edge.Onnx;

/// <summary>
/// Reads <c>ActivityManager.GetMemoryInfo</c> (availMem, lowMemory),
/// <c>PowerManager.CurrentThermalStatus</c> (API 29+) and <c>GetThermalHeadroom(10)</c> (API 30+).
/// </summary>
/// <remarks>
/// The one-second read cache in the base class is not a micro-optimisation here:
/// <c>GetThermalHeadroom</c> is rate-limited to roughly 1 Hz and returns <c>NaN</c> when polled
/// faster, so an uncached monitor would report "unknown" for most of its calls.
/// </remarks>
internal sealed class AndroidResourceMonitor(TimeProvider? timeProvider = null)
    : EdgeResourceMonitorBase(timeProvider)
{
    /// <inheritdoc />
    protected override EdgeResourceSnapshot Capture(EdgeMemoryPressure? lastPressure)
    {
        long? available = null;
        bool? isLowMemory = null;
        var thermal = EdgeThermalState.Unknown;
        float? headroom = null;

        var context = Application.Context;

        if (context.GetSystemService(Context.ActivityService) is ActivityManager activityManager)
        {
            using var memoryInfo = new ActivityManager.MemoryInfo();
            activityManager.GetMemoryInfo(memoryInfo);
            available = memoryInfo.AvailMem;
            isLowMemory = memoryInfo.LowMemory;
        }

        if (context.GetSystemService(Context.PowerService) is PowerManager powerManager)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                // Android's ladder is NONE < LIGHT < MODERATE < SEVERE < CRITICAL < EMERGENCY <
                // SHUTDOWN; a value this binding does not know maps to Unknown, never to Nominal.
                thermal = powerManager.CurrentThermalStatus switch
                {
                    ThermalStatus.None => EdgeThermalState.Nominal,
                    ThermalStatus.Light or ThermalStatus.Moderate => EdgeThermalState.Fair,
                    ThermalStatus.Severe => EdgeThermalState.Serious,
                    ThermalStatus.Critical or ThermalStatus.Emergency or ThermalStatus.Shutdown
                        => EdgeThermalState.Critical,
                    _ => EdgeThermalState.Unknown,
                };
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var value = powerManager.GetThermalHeadroom(10);

                // NaN means "asked too soon" or "this device does not implement it" - unknown, not zero.
                headroom = float.IsNaN(value) ? null : value;
            }
        }

        return new EdgeResourceSnapshot(
            available,
            isLowMemory,
            thermal,
            headroom,
            IsLowPowerMode: null,
            lastPressure);
    }
}
