using System.Runtime.InteropServices;
using Android.Content;
using Android.OS;

namespace Qavren.Edge.Chat;

/// <summary>
/// Reads <c>ActivityManager.MemoryInfo.TotalMem</c>, <c>ActivityManager.IsLowRamDevice</c> and
/// <c>Android.OS.Build.SupportedAbis[0]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>TotalMem</c> is physical RAM <b>minus</b> what the kernel reserved before userspace saw it -
/// about 5.4-5.7 GiB on a device marketed as 6 GB - which is why
/// <see cref="EdgeChatDeviceProfile.TotalMemorySource"/> exists and why
/// <see cref="ChatMemoryBudgetOptions.MinTotalMemoryBytes"/>'s defaults sit at about 0.85x nominal.
/// Comparing this figure against a 6 GiB floor would refuse the default preset on essentially every
/// nominal-6 GB Android device while admitting every nominal-6 GB iPhone.
/// </para>
/// <para>
/// <see cref="EdgeChatDeviceProfile.AvailableMemoryKind"/> is
/// <see cref="EdgeMemoryBudgetKind.SystemWide"/>: sub-project 2's Android monitor fills
/// <c>AvailableMemoryBytes</c> from <c>ActivityManager.MemoryInfo.AvailMem</c>, whose own
/// documentation says it "should not be considered absolute", so the budget believes only
/// <see cref="ChatMemoryBudgetOptions.SystemWideMemoryFraction"/> of it.
/// </para>
/// <para>
/// The first entry of <c>SupportedAbis</c> is the ABI the process actually runs as. GenAI's AAR
/// carries <c>arm64-v8a</c> and <c>x86_64</c> only - no <c>armeabi-v7a</c> - which is what the
/// order-400 ABI gate compares against.
/// </para>
/// </remarks>
internal sealed class AndroidChatDeviceProfileProvider : IEdgeChatDeviceProfileProvider
{
    /// <inheritdoc />
    public EdgeChatDeviceProfile Read()
    {
        long? total = null;
        bool? isLowRamDevice = null;

        var context = Application.Context;

        if (context.GetSystemService(Context.ActivityService) is ActivityManager activityManager)
        {
            using var memoryInfo = new ActivityManager.MemoryInfo();
            activityManager.GetMemoryInfo(memoryInfo);

            // 0 is "it would not say", never "this device has no memory".
            total = memoryInfo.TotalMem > 0 ? memoryInfo.TotalMem : null;
            isLowRamDevice = activityManager.IsLowRamDevice;
        }

        string? abi = null;
        var abis = Build.SupportedAbis;
        if (abis is { Count: > 0 })
        {
            abi = abis[0];
        }

        return new EdgeChatDeviceProfile(
            total,
            total is null ? EdgeTotalMemorySource.Unknown : EdgeTotalMemorySource.AndroidActivityManager,
            EdgeMemoryBudgetKind.SystemWide,
            isLowRamDevice,
            RuntimeInformation.RuntimeIdentifier,
            abi);
    }
}
