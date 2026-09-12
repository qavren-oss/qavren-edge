using System.Runtime.CompilerServices;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// A fact that only a device lane can honestly run (spec 16.4). It compiles on every TFM - the
/// <c>net10.0</c> host leg and the Windows device lane included - and skips at discovery anywhere
/// that is not Android, iOS or Mac Catalyst.
/// </summary>
/// <remarks>
/// A copy of <c>Qavren.Edge.Onnx.Tests</c>'s attribute rather than a reference to it: this project
/// does not reference that test project, and a test-project-to-test-project edge for one attribute
/// would drag sub-project 2's whole test surface into every chat lane. It exists so no test body
/// needs an <c>#if</c>: a conditional inside a test body reports "passed" for an assertion that
/// never executed, which is exactly the failure mode the device lanes exist to close.
/// </remarks>
public sealed class DeviceFactAttribute : FactAttribute
{
    private const string HostSkip =
        "Spec 16.4: this assertion is about a real Android, iOS or Mac Catalyst process. " +
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
    /// about an OS API rather than about silicon.
    /// </summary>
    public static bool IsDeviceLane
        => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();
}
