using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The <c>net10.0</c> leg, asserted rather than assumed. It is the leg every hosted CI runner
/// executes in tiers 1 and 2 and the leg every Windows, macOS-desktop and Linux consumer binds, so
/// "the <c>#else</c> leg is not a stub" is a test rather than a sentence in the spec.
/// </summary>
public class PortableDeviceProfileTests
{
    private static EdgeChatDeviceProfile Read() => new PortableChatDeviceProfileProvider().Read();

    [Fact]
    public void AvailableMemoryIsClassifiedSystemWideWhichIsHonestRatherThanAFallback()
    {
        // Sub-project 2's desktop monitor fills AvailableMemoryBytes from GC.GetGCMemoryInfo(),
        // which describes the machine and not a per-process allowance the OS will enforce. So the
        // budget applies SystemWideMemoryFraction to it, which is the conservative branch.
        Assert.Equal(EdgeMemoryBudgetKind.SystemWide, Read().AvailableMemoryKind);
    }

    [Fact]
    public void IsLowRamDeviceIsNullAndNotFalse()
    {
        // "Not a low-RAM device" and "nobody asked the question" are different claims, and the
        // budget's hard refusal fires only on an explicit true.
        Assert.Null(Read().IsLowRamDevice);
    }

    [Fact]
    public void AbiIsNullOffAndroid()
        => Assert.Null(Read().Abi);

    [Fact]
    public void TheRuntimeIdentifierIsReported()
        => Assert.False(string.IsNullOrWhiteSpace(Read().RuntimeIdentifier));

    [Fact]
    public void TotalMemoryIsEitherAPlausibleFigureOrNullButNeverZero()
    {
        var profile = Read();

        if (profile.TotalMemoryBytes is { } total)
        {
            Assert.True(total > 0, "0 means 'the GC would not say' and must have become null.");
            Assert.Equal(EdgeTotalMemorySource.GcMemoryInfo, profile.TotalMemorySource);
        }
        else
        {
            Assert.Equal(EdgeTotalMemorySource.Unknown, profile.TotalMemorySource);
        }
    }

    [Fact]
    public void ADesktopTotalIsNeverComparedAgainstTheDeviceFloorWhateverItReports()
    {
        var profile = Read();
        var preset = ChatPresets.Llama32_1BInstructInt4;

        var decision = ChatMemoryBudget.Resolve(
            new ChatBudgetRequest(
                preset.Id,
                preset.Shape,
                4096,
                new EdgeResourceSnapshot(5_000_000_000L, null, EdgeThermalState.Unknown, null, null, null),
                profile),
            new ChatMemoryBudgetOptions());

        Assert.NotEqual(ChatMemoryVerdict.RefusedDeviceTooSmall, decision.Verdict);
    }

    [Fact]
    public void TheProfileIsConstantSoTwoReadsAgree()
        => Assert.Equal(Read(), Read());
}
