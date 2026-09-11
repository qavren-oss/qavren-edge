namespace Qavren.Edge.Chat;

/// <summary>
/// Where <see cref="EdgeChatDeviceProfile.TotalMemoryBytes"/> came from, because the three sources
/// are not the same measurement and the floor in
/// <see cref="ChatMemoryBudgetOptions.MinTotalMemoryBytes"/> is a constant compared against all of
/// them.
/// </summary>
public enum EdgeTotalMemorySource
{
    /// <summary>No figure.</summary>
    Unknown = 0,

    /// <summary>
    /// <c>ActivityManager.MemoryInfo.TotalMem</c>. Physical RAM <b>minus</b> what the kernel
    /// reserved before userspace saw it - about 5.4-5.7 GiB on a device marketed as 6 GB.
    /// </summary>
    AndroidActivityManager,

    /// <summary>
    /// <c>NSProcessInfo.ProcessInfo.PhysicalMemory</c>. The full nominal figure, so a nominal
    /// 6 GB device reports 6,442,450,944 exactly.
    /// </summary>
    ApplePhysicalMemory,

    /// <summary>
    /// <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>. <b>Not physical RAM</b>: it is the
    /// GC's available-memory figure, which on a containerised host is the container's limit. It is
    /// reported for diagnostics and the device floor is <b>skipped</b> for it (spec section 9.3),
    /// because a 2 GiB container limit on a 64 GiB build agent is a false refusal and a 64 GiB
    /// figure on a machine with no limit tells the budget nothing it does not already get from
    /// <c>AvailableMemoryBytes</c>.
    /// </summary>
    GcMemoryInfo,
}

/// <summary>What <c>EdgeResourceSnapshot.AvailableMemoryBytes</c> actually measures.</summary>
public enum EdgeMemoryBudgetKind
{
    /// <summary>The platform reports nothing usable.</summary>
    Unknown = 0,

    /// <summary>
    /// A per-process budget the caller may spend. Apple's <c>os_proc_available_memory()</c>:
    /// "the current memory limit minus the memory footprint of your app".
    /// </summary>
    PerProcess,

    /// <summary>
    /// A system-wide free-memory figure that is NOT a per-app allowance. Android's
    /// <c>ActivityManager.MemoryInfo.AvailMem</c>, whose own documentation says it "should not be
    /// considered absolute". Spending it in full is how a 1.24 GB model gets the app killed by
    /// lmkd, so the budget multiplies it by
    /// <see cref="ChatMemoryBudgetOptions.SystemWideMemoryFraction"/> before believing it.
    /// </summary>
    SystemWide,
}

/// <summary>
/// Static facts about the device, read <b>once</b> at startup and cached for the life of the
/// process. Everything here is constant, which is what stops this and
/// <c>IEdgeResourceMonitor</c> drifting apart: per-turn readings come from the monitor and nowhere
/// else.
/// </summary>
/// <param name="TotalMemoryBytes">
/// The platform's best total-memory figure, or null when it will not say. <b>Not comparable
/// across platforms without <paramref name="TotalMemorySource"/></b> - see that parameter.
/// </param>
/// <param name="TotalMemorySource">
/// Which of three different measurements <paramref name="TotalMemoryBytes"/> actually is. The
/// device floor consults it: it applies to a real physical reading and is skipped for
/// <see cref="EdgeTotalMemorySource.GcMemoryInfo"/>.
/// </param>
/// <param name="AvailableMemoryKind">How to read the monitor's available-memory figure.</param>
/// <param name="IsLowRamDevice">
/// Android's <c>ActivityManager.IsLowRamDevice</c>, documented as "1GB or less of RAM". Reported,
/// and used only as a hard refusal - it is false on every phone that is still far too small for a
/// 1.24 GB model, so it is useless as the gate.
/// </param>
/// <param name="RuntimeIdentifier">The running RID.</param>
/// <param name="Abi">The running Android ABI, or null off Android.</param>
public readonly record struct EdgeChatDeviceProfile(
    long? TotalMemoryBytes,
    EdgeTotalMemorySource TotalMemorySource,
    EdgeMemoryBudgetKind AvailableMemoryKind,
    bool? IsLowRamDevice,
    string RuntimeIdentifier,
    string? Abi);

/// <summary>
/// Reads <see cref="EdgeChatDeviceProfile"/>. Three implementations, one per compilation, chosen by
/// <c>AddGenAiRuntime()</c> behind <c>#if ANDROID</c> / <c>#if IOS || MACCATALYST</c> / <c>#else</c>
/// exactly as sub-project 2 splits <c>IEdgeResourceMonitor</c>.
/// </summary>
/// <remarks>
/// <b>The <c>#else</c> leg is not a stub.</b> It is the leg every hosted CI runner executes in
/// tiers 1 and 2, and the leg every Windows, macOS-desktop and Linux consumer binds, so spec
/// section 6.3 defines every field on it rather than leaving it to the implementation.
/// </remarks>
public interface IEdgeChatDeviceProfileProvider
{
    /// <summary>Reads the device.</summary>
    /// <returns>The profile. Registered as a singleton, so this is called once per process.</returns>
    EdgeChatDeviceProfile Read();
}
