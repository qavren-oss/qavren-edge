using System.Runtime.CompilerServices;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Platforms;

/// <summary>
/// A fact that only a device lane can honestly run (spec 14.6). It compiles on every TFM - the
/// <c>net10.0</c> host leg and the Windows device lane included - and skips at discovery anywhere
/// that is not Android, iOS or Mac Catalyst. The same shape as SP2's attribute and the extractor
/// project's; it is re-declared here because neither of those projects is, or may become, a
/// reference of this one.
/// </summary>
/// <remarks>
/// This exists so no test body needs an <c>#if</c>. A conditional inside a test body reports
/// "passed" for an assertion that never executed, which is exactly the failure mode the device
/// lanes are supposed to close: a skip is visible in the run summary, a silently-empty test body
/// is not.
/// </remarks>
public sealed class DeviceFactAttribute : FactAttribute
{
    private const string HostSkip =
        "Spec 14.6: this assertion is about a real Android, iOS or Mac Catalyst process. " +
        "The host lane compiles it and skips it; only a device lane runs it.";

    /// <summary>
    /// Sets <see cref="FactAttribute.Skip"/> off the host. xunit v3's <c>Skip</c> is a non-virtual
    /// auto-property, so the reason is assigned here rather than overridden; xunit instantiates the
    /// attribute during discovery, which is why a constructor assignment is what it reads.
    /// </summary>
    /// <param name="sourceFilePath">Filled in by the compiler; xunit reports it (xUnit3003).</param>
    /// <param name="sourceLineNumber">Filled in by the compiler; xunit reports it (xUnit3003).</param>
    public DeviceFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IsDeviceLane)
        {
            Skip = HostSkip;
        }
    }

    /// <summary>
    /// True on Android, iOS (device or simulator) and Mac Catalyst. The iOS simulator counts:
    /// <c>OperatingSystem.IsIOS()</c> is true there, and every assertion behind this attribute is
    /// about what the runtime actually does under a real platform lifecycle rather than about silicon.
    /// </summary>
    public static bool IsDeviceLane
        => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();
}
