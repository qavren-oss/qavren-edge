using System.Runtime.InteropServices;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// The <c>#else</c> leg of spec section 6.3: Windows, macOS desktop, Linux, and every hosted CI
/// runner. <b>Not a stub</b> - this is the leg tiers 1 and 2 execute on every PR, so every field it
/// fills is specified rather than left to the implementation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EdgeChatDeviceProfile.AvailableMemoryKind"/> is
/// <see cref="EdgeMemoryBudgetKind.SystemWide"/> because that is the honest classification, not a
/// fallback: sub-project 2's desktop monitor fills <c>AvailableMemoryBytes</c> from
/// <c>GC.GetGCMemoryInfo()</c> - its own XML doc says "Desktop: GC.GetGCMemoryInfo(), advisory
/// only" - and that figure describes the machine, not a per-process allowance the OS will enforce.
/// So the budget applies <see cref="ChatMemoryBudgetOptions.SystemWideMemoryFraction"/> to it,
/// which is the conservative branch, and spec section 9.3's three-way branch has no undefined arm
/// on any TFM sub-project 4 ships.
/// </para>
/// <para>
/// <see cref="EdgeChatDeviceProfile.IsLowRamDevice"/> is <see langword="null"/> and <b>not
/// <see langword="false"/></b>, because "not a low-RAM device" and "nobody asked the question" are
/// different claims and spec section 9.3's hard refusal fires only on an explicit
/// <see langword="true"/>.
/// </para>
/// <para>
/// <see cref="EdgeChatDeviceProfile.TotalMemoryBytes"/> may legitimately be
/// <see langword="null"/>: <c>GCMemoryInfo</c> returns <c>0</c> for
/// <c>TotalAvailableMemoryBytes</c> in some container configurations, and 0 is "it will not say",
/// never "this machine has no memory". That is the no-floor path in spec section 9.3, not an error.
/// </para>
/// </remarks>
internal sealed class PortableChatDeviceProfileProvider : IEdgeChatDeviceProfileProvider
{
    /// <inheritdoc />
    public EdgeChatDeviceProfile Read()
    {
        var reported = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long? total = reported > 0 ? reported : null;

        return new EdgeChatDeviceProfile(
            total,
            total is null ? EdgeTotalMemorySource.Unknown : EdgeTotalMemorySource.GcMemoryInfo,
            EdgeMemoryBudgetKind.SystemWide,
            IsLowRamDevice: null,
            RuntimeInformation.RuntimeIdentifier,
            Abi: null);
    }
}
