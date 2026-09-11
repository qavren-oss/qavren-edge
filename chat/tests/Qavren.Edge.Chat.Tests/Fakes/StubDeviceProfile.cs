namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// A scripted <see cref="EdgeChatDeviceProfile"/> plus the three named shapes the tables reuse.
/// Nominal-to-reported is not a rounding error and the constants say so: Android's
/// <c>TotalMem</c> excludes kernel-reserved memory, Apple's <c>PhysicalMemory</c> does not.
/// </summary>
internal sealed class StubDeviceProfile(EdgeChatDeviceProfile profile) : IEdgeChatDeviceProfileProvider
{
    public EdgeChatDeviceProfile Read() => profile;

    /// <summary>An Android device whose <c>TotalMem</c> reports <paramref name="totalMemoryBytes"/>.</summary>
    public static EdgeChatDeviceProfile Android(long? totalMemoryBytes, bool? isLowRamDevice = false) =>
        new(
            totalMemoryBytes,
            totalMemoryBytes is null ? EdgeTotalMemorySource.Unknown : EdgeTotalMemorySource.AndroidActivityManager,
            EdgeMemoryBudgetKind.SystemWide,
            isLowRamDevice,
            "android-arm64",
            "arm64-v8a");

    /// <summary>An iPhone whose <c>PhysicalMemory</c> reports the full nominal figure.</summary>
    public static EdgeChatDeviceProfile Apple(long? totalMemoryBytes) =>
        new(
            totalMemoryBytes,
            totalMemoryBytes is null ? EdgeTotalMemorySource.Unknown : EdgeTotalMemorySource.ApplePhysicalMemory,
            EdgeMemoryBudgetKind.PerProcess,
            IsLowRamDevice: null,
            "ios-arm64",
            Abi: null);

    /// <summary>
    /// A desktop or hosted runner: the figure is <c>GC.GetGCMemoryInfo()</c>'s, which is a
    /// container limit under a container, so the device floor never consults it.
    /// </summary>
    public static EdgeChatDeviceProfile Desktop(long? totalMemoryBytes) =>
        new(
            totalMemoryBytes,
            totalMemoryBytes is null ? EdgeTotalMemorySource.Unknown : EdgeTotalMemorySource.GcMemoryInfo,
            EdgeMemoryBudgetKind.SystemWide,
            IsLowRamDevice: null,
            "win-x64",
            Abi: null);

    /// <summary>1 GiB, as a device class rather than as a reading.</summary>
    public const long Gibibyte = 1024L * 1024 * 1024;

    /// <summary>A nominal-4 GB Android device: 3.6 GiB of <c>TotalMem</c>.</summary>
    public const long Android4Gb = 3_865_470_566L;

    /// <summary>A nominal-6 GB Android device: 5.6 GiB of <c>TotalMem</c>.</summary>
    public const long Android6Gb = 6_012_954_214L;

    /// <summary>A nominal-8 GB Android device: 7.2 GiB of <c>TotalMem</c>.</summary>
    public const long Android8Gb = 7_730_941_133L;

    /// <summary>A nominal-12 GB Android device: 11.2 GiB of <c>TotalMem</c>.</summary>
    public const long Android12Gb = 12_025_908_428L;

    /// <summary>A nominal-4 GB iPhone: 4.0 GiB exactly.</summary>
    public const long Apple4Gb = 4_294_967_296L;

    /// <summary>A nominal-6 GB iPhone: 6.0 GiB exactly.</summary>
    public const long Apple6Gb = 6_442_450_944L;

    /// <summary>A nominal-8 GB iPhone: 8.0 GiB exactly.</summary>
    public const long Apple8Gb = 8_589_934_592L;

    /// <summary>A nominal-12 GB iPad: 12.0 GiB exactly.</summary>
    public const long Apple12Gb = 12_884_901_888L;
}
