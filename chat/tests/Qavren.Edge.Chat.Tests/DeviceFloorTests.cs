using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The device floor gets its own table because one constant is compared against three different
/// platform totals. Android's <c>TotalMem</c> excludes kernel-reserved memory and reports about
/// 5.6 GiB on a nominal-6 GB device; Apple's <c>PhysicalMemory</c> reports 6.0 GiB exactly for the
/// same device class; and <c>GCMemoryInfo</c>'s figure is not physical memory at all. A floor of
/// exactly 6 GiB would refuse the default preset on essentially every nominal-6 GB Android phone
/// while admitting every nominal-6 GB iPhone, which is the precise opposite of the calibration.
/// </summary>
public class DeviceFloorTests
{
    private static readonly ChatPreset Llama = ChatPresets.Llama32_1BInstructInt4;
    private static readonly ChatPreset Qwen = ChatPresets.Qwen3_600MInt4;

    /// <summary>
    /// The tier-2 fixture's band: about 500 KB of random weights, and no device class to gate.
    /// <see cref="ChatModelShape.MeasuredPeakBytes"/> is cleared with the weights - a measurement of
    /// a 1167 MiB model says nothing about a 500 KB one, and leaving it would make the budget read a
    /// 1280 MiB peak for a fixture that has none.
    /// </summary>
    private static readonly ChatModelShape TinyFixtureShape =
        Llama.Shape with { WeightsBytes = 500_000L, MeasuredPeakBytes = null, MeasuredOn = null };

    private static ChatMemoryDecision Resolve(ChatModelShape shape, long available, EdgeChatDeviceProfile device)
    {
        var monitor = new StubResourceMonitor(available);
        return ChatMemoryBudget.Resolve(
            new ChatBudgetRequest("floor-test", shape, 4096, monitor.Read(), device),
            new ChatMemoryBudgetOptions());
    }

    // ---- the three bands, asserted as the literals they are -------------------------------------

    [Fact]
    public void TheThreeBandsAreFiveGibibytesThreePointFourGibibytesAndNoFloorAtAll()
    {
        var floor = new ChatMemoryBudgetOptions().MinTotalMemoryBytes;

        Assert.Equal(5_368_709_120L, floor(Llama.Shape));
        Assert.Equal(3_650_722_201L, floor(Qwen.Shape));
        Assert.Null(floor(TinyFixtureShape));
    }

    [Fact]
    public void TheBandBoundariesAreOneGibibyteAndTwoHundredMebibytesOfWeights()
    {
        var floor = new ChatMemoryBudgetOptions().MinTotalMemoryBytes;
        var oneGibibyte = Llama.Shape with { WeightsBytes = 1024L * 1024 * 1024 };
        var justOver = Llama.Shape with { WeightsBytes = (1024L * 1024 * 1024) + 1 };
        var twoHundredMebibytes = Llama.Shape with { WeightsBytes = 200L * 1024 * 1024 };
        var justOverTwoHundred = Llama.Shape with { WeightsBytes = (200L * 1024 * 1024) + 1 };

        Assert.Equal(3_650_722_201L, floor(oneGibibyte));
        Assert.Equal(5_368_709_120L, floor(justOver));
        Assert.Null(floor(twoHundredMebibytes));
        Assert.Equal(3_650_722_201L, floor(justOverTwoHundred));
    }

    // ---- one constant, three platform totals -----------------------------------------------------

    [Fact]
    public void ANominalSixGigabyteAndroidDeviceReportingFivePointSixGibibytesPassesTheLargePreset()
    {
        var decision = Resolve(Llama.Shape, 5_000_000_000L, StubDeviceProfile.Android(StubDeviceProfile.Android6Gb));

        Assert.NotEqual(ChatMemoryVerdict.RefusedDeviceTooSmall, decision.Verdict);
        Assert.Equal(ChatMemoryVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void ANominalSixGigabyteIPhoneReportingSixPointZeroGibibytesPassesTheSameConstant()
    {
        var decision = Resolve(Llama.Shape, 3_000_000_000L, StubDeviceProfile.Apple(StubDeviceProfile.Apple6Gb));

        Assert.NotEqual(ChatMemoryVerdict.RefusedDeviceTooSmall, decision.Verdict);
        Assert.Equal(ChatMemoryVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void ANominalFourGigabyteAndroidDeviceFailsTheLargePresetAndPassesTheSmallOne()
    {
        var device = StubDeviceProfile.Android(StubDeviceProfile.Android4Gb);

        var large = Resolve(Llama.Shape, 5_000_000_000L, device);
        var small = Resolve(Qwen.Shape, 5_000_000_000L, device);

        Assert.Equal(ChatMemoryVerdict.RefusedDeviceTooSmall, large.Verdict);
        Assert.Equal(0, large.ContextTokens);
        Assert.Equal(StubDeviceProfile.Android4Gb, large.TotalMemoryBytes);

        Assert.Equal(ChatMemoryVerdict.Allowed, small.Verdict);
    }

    [Theory]
    [InlineData(1_073_741_824L)]
    [InlineData(2_147_483_648L)]
    [InlineData(68_719_476_736L)]
    public void AGcMemoryInfoTotalIsNeverComparedAgainstTheFloorWhateverItsValue(long totalMemoryBytes)
    {
        // A 2 GiB container limit on a 64 GiB build agent is a false refusal, and a 64 GiB figure on
        // a machine with no limit tells the budget nothing AvailableMemoryBytes has not already
        // said. The floor is a device class, and a container limit is not one.
        var decision = Resolve(Llama.Shape, 5_000_000_000L, StubDeviceProfile.Desktop(totalMemoryBytes));

        Assert.NotEqual(ChatMemoryVerdict.RefusedDeviceTooSmall, decision.Verdict);
        Assert.Equal(ChatMemoryVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void APresetUnderTwoHundredMebibytesOfWeightsHasNoFloorOnAnEmulatorSizedDevice()
    {
        // A default Android AVD on a hosted runner reports about 2 GiB of TotalMem. A blanket floor
        // would specify sub-project 4's highest-value device assertion into a guaranteed 7006.
        var decision = Resolve(TinyFixtureShape, 1_500_000_000L, StubDeviceProfile.Android(2_147_483_648L));

        Assert.Equal(ChatMemoryVerdict.Allowed, decision.Verdict);
        Assert.Equal(4096, decision.ContextTokens);
    }

    [Fact]
    public void AnUnknownTotalIsNeverComparedAgainstTheFloorEither()
    {
        var decision = Resolve(Llama.Shape, 5_000_000_000L, StubDeviceProfile.Android(null));

        Assert.NotEqual(ChatMemoryVerdict.RefusedDeviceTooSmall, decision.Verdict);
    }

    [Fact]
    public void SettingTheFloorToNullDisablesTheGateForAPreset()
    {
        var monitor = new StubResourceMonitor(5_000_000_000L);
        var decision = ChatMemoryBudget.Resolve(
            new ChatBudgetRequest("floor-test", Llama.Shape, 4096, monitor.Read(), StubDeviceProfile.Android(StubDeviceProfile.Android4Gb)),
            new ChatMemoryBudgetOptions { MinTotalMemoryBytes = static _ => null });

        Assert.Equal(ChatMemoryVerdict.Allowed, decision.Verdict);
    }

    // ---- IsLowRamDevice ---------------------------------------------------------------------------

    [Fact]
    public void IsLowRamDeviceTrueRefusesAndNullDoesNot()
    {
        var flagged = Resolve(Llama.Shape, 5_000_000_000L, StubDeviceProfile.Android(StubDeviceProfile.Android8Gb, isLowRamDevice: true));
        var unasked = Resolve(Llama.Shape, 5_000_000_000L, StubDeviceProfile.Android(StubDeviceProfile.Android8Gb, isLowRamDevice: null));
        var answered = Resolve(Llama.Shape, 5_000_000_000L, StubDeviceProfile.Android(StubDeviceProfile.Android8Gb, isLowRamDevice: false));

        Assert.Equal(ChatMemoryVerdict.RefusedDeviceTooSmall, flagged.Verdict);
        Assert.NotEqual(ChatMemoryVerdict.RefusedDeviceTooSmall, unasked.Verdict);
        Assert.NotEqual(ChatMemoryVerdict.RefusedDeviceTooSmall, answered.Verdict);
    }
}
