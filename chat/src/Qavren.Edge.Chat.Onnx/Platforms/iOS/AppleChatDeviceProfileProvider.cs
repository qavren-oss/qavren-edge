using System.Runtime.InteropServices;

namespace Qavren.Edge.Chat;

/// <summary>
/// Reads <c>NSProcessInfo.ProcessInfo.PhysicalMemory</c>. One implementation for both
/// <c>net10.0-ios</c> and <c>net10.0-maccatalyst</c>, which is why it lives under
/// <c>Platforms\iOS\</c> and the csproj includes that folder for both platform identifiers -
/// the same split sub-project 2 uses for <c>AppleResourceMonitor</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>PhysicalMemory</c> is the full nominal figure: a nominal-6 GB device reports
/// 6,442,450,944 exactly, where Android's <c>TotalMem</c> reports 5.4-5.7 GiB for the same device
/// class. <see cref="EdgeChatDeviceProfile.TotalMemorySource"/> is what lets one
/// <see cref="ChatMemoryBudgetOptions.MinTotalMemoryBytes"/> constant mean one device class across
/// both.
/// </para>
/// <para>
/// <see cref="EdgeChatDeviceProfile.AvailableMemoryKind"/> is
/// <see cref="EdgeMemoryBudgetKind.PerProcess"/> because sub-project 2's Apple monitor fills
/// <c>AvailableMemoryBytes</c> from <c>os_proc_available_memory()</c>, documented as "the current
/// memory limit minus the memory footprint of your app" - a real allowance, so the budget spends it
/// without applying <see cref="ChatMemoryBudgetOptions.SystemWideMemoryFraction"/>.
/// </para>
/// <para>
/// <see cref="EdgeChatDeviceProfile.IsLowRamDevice"/> is <see langword="null"/>: Apple exposes no
/// equivalent, and "nobody asked" is not <see langword="false"/>.
/// </para>
/// </remarks>
internal sealed class AppleChatDeviceProfileProvider : IEdgeChatDeviceProfileProvider
{
    /// <inheritdoc />
    public EdgeChatDeviceProfile Read()
    {
        var physical = (long)NSProcessInfo.ProcessInfo.PhysicalMemory;
        long? total = physical > 0 ? physical : null;

        return new EdgeChatDeviceProfile(
            total,
            total is null ? EdgeTotalMemorySource.Unknown : EdgeTotalMemorySource.ApplePhysicalMemory,
            EdgeMemoryBudgetKind.PerProcess,
            IsLowRamDevice: null,
            RuntimeInformation.RuntimeIdentifier,
            Abi: null);
    }
}
