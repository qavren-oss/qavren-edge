using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The single most important target in sub-project 4. <c>ChatMemoryBudget</c> is pure - no ORT, no
/// GenAI, no I/O - so the whole gate is asserted here against a hand-written snapshot and a stub
/// profile, with no natives at all.
/// </summary>
/// <remarks>
/// Every expected byte count below was computed in the plan from the two presets' real geometry and
/// is written as a literal on purpose: a test that recomputed the formula it is testing would agree
/// with any future edit, including a wrong one.
/// </remarks>
public class ChatMemoryBudgetTests
{
    private static readonly ChatPreset Llama = ChatPresets.Llama32_1BInstructInt4;
    private static readonly ChatPreset Qwen = ChatPresets.Qwen3_600MInt4;

    private static ChatMemoryDecision Resolve(
        ChatPreset preset,
        int requestedContextTokens,
        long? availableMemoryBytes,
        EdgeChatDeviceProfile device,
        ChatMemoryBudgetOptions? options = null)
    {
        var monitor = new StubResourceMonitor(availableMemoryBytes);
        var request = new ChatBudgetRequest(
            preset.Id,
            preset.Shape,
            requestedContextTokens,
            monitor.Read(),
            device);

        return ChatMemoryBudget.Resolve(request, options ?? new ChatMemoryBudgetOptions());
    }

    // ---- the arithmetic anchors -------------------------------------------------------------

    [Fact]
    public void LlamaCostsThirtyTwoKibibytesOfKvPerToken()
        => Assert.Equal(32_768L, Llama.Shape.KvCacheBytesPerToken);

    [Fact]
    public void QwenCostsOneHundredAndTwelveKibibytesOfKvPerTokenWhichIsThreeAndAHalfTimesLlamas()
    {
        Assert.Equal(114_688L, Qwen.Shape.KvCacheBytesPerToken);
        Assert.Equal(3.5d, (double)Qwen.Shape.KvCacheBytesPerToken / Llama.Shape.KvCacheBytesPerToken);
    }

    [Theory]
    [InlineData(4096, 1_761_107_258L)]
    [InlineData(3072, 1_727_552_826L)]
    [InlineData(2048, 1_693_998_394L)]
    [InlineData(1536, 1_677_221_178L)]
    [InlineData(1024, 1_660_443_962L)]
    public void TheLlamaLadderWalkedEndToEndBuysNinetySixMebibytes(int contextTokens, long expected)
        => Assert.Equal(expected, ChatMemoryBudget.RequiredBytes(Llama.Shape, contextTokens, new ChatMemoryBudgetOptions()));

    [Fact]
    public void TheLlamaLadderIsAboutSixPercentOfLeverageWhichIsWhatAWeightDominatedPresetMeans()
    {
        var top = ChatMemoryBudget.RequiredBytes(Llama.Shape, 4096, new ChatMemoryBudgetOptions());
        var bottom = ChatMemoryBudget.RequiredBytes(Llama.Shape, 1024, new ChatMemoryBudgetOptions());

        Assert.Equal(100_663_296L, top - bottom);
        Assert.InRange((double)(top - bottom) / top, 0.05d, 0.07d);
    }

    [Theory]
    [InlineData(4096, 1_356_075_101L)]
    [InlineData(1024, 1_003_753_565L)]
    public void TheQwenLadderBuysTwentySixPercentBecauseItsShapeIsKvDominated(int contextTokens, long expected)
        => Assert.Equal(expected, ChatMemoryBudget.RequiredBytes(Qwen.Shape, contextTokens, new ChatMemoryBudgetOptions()));

    [Fact]
    public void QwenIsSmallerOnDiskAndWantsMoreKvWhichIsTheCaseTheBudgetExistsToCatch()
    {
        Assert.True(Qwen.Shape.WeightsBytes < Llama.Shape.WeightsBytes);
        Assert.True(Qwen.Shape.KvCacheBytes(4096) > Llama.Shape.KvCacheBytes(4096));
    }

    // ---- the device x kind x preset x rung table ---------------------------------------------

    [Theory]
    // Llama, Android (SystemWide, 0.60 of the reading), nominal-6 GB and up.
    [InlineData("llama", "android", StubDeviceProfile.Android6Gb, 3_000_000_000L, ChatMemoryVerdict.Allowed, 4096)]
    [InlineData("llama", "android", StubDeviceProfile.Android8Gb, 2_800_000_000L, ChatMemoryVerdict.AllowedReduced, 1536)]
    [InlineData("llama", "android", StubDeviceProfile.Android12Gb, 2_700_000_000L, ChatMemoryVerdict.RefusedInsufficientMemory, 0)]
    // Llama, Apple (PerProcess - the fraction is NOT applied).
    [InlineData("llama", "apple", StubDeviceProfile.Apple6Gb, 1_800_000_000L, ChatMemoryVerdict.Allowed, 4096)]
    [InlineData("llama", "apple", StubDeviceProfile.Apple8Gb, 1_700_000_000L, ChatMemoryVerdict.AllowedReduced, 2048)]
    [InlineData("llama", "apple", StubDeviceProfile.Apple12Gb, 1_000_000_000L, ChatMemoryVerdict.RefusedInsufficientMemory, 0)]
    // Qwen, Android: a nominal-4 GB device clears Qwen's floor, which Llama's it does not.
    [InlineData("qwen", "android", StubDeviceProfile.Android4Gb, 2_300_000_000L, ChatMemoryVerdict.Allowed, 4096)]
    [InlineData("qwen", "android", StubDeviceProfile.Android6Gb, 1_800_000_000L, ChatMemoryVerdict.AllowedReduced, 1536)]
    [InlineData("qwen", "android", StubDeviceProfile.Android8Gb, 1_600_000_000L, ChatMemoryVerdict.RefusedInsufficientMemory, 0)]
    // Qwen, Apple.
    [InlineData("qwen", "apple", StubDeviceProfile.Apple4Gb, 1_400_000_000L, ChatMemoryVerdict.Allowed, 4096)]
    [InlineData("qwen", "apple", StubDeviceProfile.Apple6Gb, 1_150_000_000L, ChatMemoryVerdict.AllowedReduced, 2048)]
    [InlineData("qwen", "apple", StubDeviceProfile.Apple8Gb, 900_000_000L, ChatMemoryVerdict.RefusedInsufficientMemory, 0)]
    // Desktop, where the total is a GcMemoryInfo figure and the floor is skipped whatever it says.
    [InlineData("llama", "desktop", 2_147_483_648L, 3_000_000_000L, ChatMemoryVerdict.Allowed, 4096)]
    public void TheTable(string presetId, string deviceKind, long totalMemoryBytes, long available, ChatMemoryVerdict verdict, int contextTokens)
    {
        var preset = presetId == "llama" ? Llama : Qwen;
        var device = deviceKind switch
        {
            "android" => StubDeviceProfile.Android(totalMemoryBytes),
            "apple" => StubDeviceProfile.Apple(totalMemoryBytes),
            _ => StubDeviceProfile.Desktop(totalMemoryBytes),
        };

        var decision = Resolve(preset, 4096, available, device);

        Assert.Equal(verdict, decision.Verdict);
        Assert.Equal(contextTokens, decision.ContextTokens);
    }

    [Fact]
    public void SystemWideMemoryFractionActuallyBitesOnAndroid()
    {
        const long Available = 1_800_000_000L;

        var apple = Resolve(Llama, 4096, Available, StubDeviceProfile.Apple(StubDeviceProfile.Apple6Gb));
        var android = Resolve(Llama, 4096, Available, StubDeviceProfile.Android(StubDeviceProfile.Android6Gb));

        Assert.Equal(ChatMemoryVerdict.Allowed, apple.Verdict);
        Assert.Equal(Available, apple.UsableBytes);

        Assert.Equal(ChatMemoryVerdict.RefusedInsufficientMemory, android.Verdict);
        Assert.Equal(1_080_000_000L, android.UsableBytes);
    }

    [Fact]
    public void EveryRungOfTheLlamaLadderIsReachableOnSomeDevice()
    {
        // 4096 needs 1,761,107,258 and 1024 needs 1,660,443,962 - so a device between the two
        // reaches exactly one rung, which is what makes the ladder a lever rather than a slogan.
        var device = StubDeviceProfile.Apple(StubDeviceProfile.Apple6Gb);
        int[] ladder = [4096, 3072, 2048, 1536, 1024];

        foreach (var rung in ladder)
        {
            var required = ChatMemoryBudget.RequiredBytes(Llama.Shape, rung, new ChatMemoryBudgetOptions());
            var decision = Resolve(Llama, 4096, required, device);

            Assert.Equal(rung, decision.ContextTokens);
            Assert.Equal(
                rung == 4096 ? ChatMemoryVerdict.Allowed : ChatMemoryVerdict.AllowedReduced,
                decision.Verdict);
        }
    }

    // ---- the measured-peak term ---------------------------------------------------------------

    [Fact]
    public void PreferMeasuredPeakDoesNotBindForEitherShippedPresetAtFourThousandAndNinetySix()
    {
        // Llama: 1,342,177,280 measured + 201,326,592 reserve = 1,543,503,872, which is BELOW its
        // own 1,761,107,258 arithmetic. Qwen has no measurement at all. A future edit that makes
        // either bind is visible here rather than in somebody's memory profile.
        var llama = Resolve(Llama, 4096, 3_000_000_000L, StubDeviceProfile.Apple(StubDeviceProfile.Apple8Gb));
        var qwen = Resolve(Qwen, 4096, 3_000_000_000L, StubDeviceProfile.Apple(StubDeviceProfile.Apple8Gb));

        Assert.False(llama.UsedMeasuredPeak);
        Assert.Equal(1_761_107_258L, llama.RequiredBytes);
        Assert.Equal(1_342_177_280L, Llama.Shape.MeasuredPeakBytes);

        Assert.False(qwen.UsedMeasuredPeak);
        Assert.Null(Qwen.Shape.MeasuredPeakBytes);
    }

    [Fact]
    public void MeasuredPeakBindsForAShapeWhoseArithmeticUnderReportsWhichIsTheGemmaCase()
    {
        // Gemma-3-1b's shape: ~929 MiB computed, 1502 MiB measured. The machinery exists for it.
        var shape = Llama.Shape with { MeasuredPeakBytes = 1_574_961_152L };
        var options = new ChatMemoryBudgetOptions();

        var monitor = new StubResourceMonitor(3_000_000_000L);
        var decision = ChatMemoryBudget.Resolve(
            new ChatBudgetRequest("gemma-shaped", shape, 4096, monitor.Read(), StubDeviceProfile.Apple(StubDeviceProfile.Apple8Gb)),
            options);

        Assert.True(decision.UsedMeasuredPeak);
        Assert.Equal(1_574_961_152L + options.ReserveBytes, decision.RequiredBytes);
    }

    [Fact]
    public void TurningPreferMeasuredPeakOffPutsTheArithmeticBack()
    {
        var shape = Llama.Shape with { MeasuredPeakBytes = 1_574_961_152L };
        var monitor = new StubResourceMonitor(3_000_000_000L);
        var request = new ChatBudgetRequest("gemma-shaped", shape, 4096, monitor.Read(), StubDeviceProfile.Apple(StubDeviceProfile.Apple8Gb));

        var decision = ChatMemoryBudget.Resolve(request, new ChatMemoryBudgetOptions { PreferMeasuredPeak = false });

        Assert.False(decision.UsedMeasuredPeak);
        Assert.Equal(1_761_107_258L, decision.RequiredBytes);
    }

    // ---- the unknown reading -------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void AReadingNobodyCanMakeIsNeverARefusal(long? available)
    {
        var decision = Resolve(Llama, 4096, available, StubDeviceProfile.Desktop(null));

        Assert.Equal(ChatMemoryVerdict.SkippedUnknown, decision.Verdict);
        Assert.Equal(4096, decision.ContextTokens);
        Assert.Null(decision.UsableBytes);
    }

    [Fact]
    public void RefuseWhenUnknownInvertsExactlyThatOneCaseAndNothingElse()
    {
        var options = new ChatMemoryBudgetOptions { RefuseWhenUnknown = true };

        var unknown = Resolve(Llama, 4096, null, StubDeviceProfile.Desktop(null), options);
        var known = Resolve(Llama, 4096, 3_000_000_000L, StubDeviceProfile.Apple(StubDeviceProfile.Apple8Gb), options);

        Assert.Equal(ChatMemoryVerdict.RefusedInsufficientMemory, unknown.Verdict);
        Assert.Equal(0, unknown.ContextTokens);

        Assert.Equal(ChatMemoryVerdict.Allowed, known.Verdict);
    }

    // ---- the shape of the decision itself --------------------------------------------------------

    [Fact]
    public void EveryTermTheGateReadIsOnTheDecision()
    {
        var options = new ChatMemoryBudgetOptions();
        var decision = Resolve(Llama, 4096, 3_000_000_000L, StubDeviceProfile.Android(StubDeviceProfile.Android6Gb), options);

        Assert.Equal(Llama.Shape.WeightsBytes, decision.WeightsBytes);
        Assert.Equal(134_217_728L, decision.KvCacheBytes);
        Assert.Equal(options.WorkspaceBytes, decision.WorkspaceBytes);
        Assert.Equal(options.ReserveBytes, decision.ReserveBytes);
        Assert.Equal(3_000_000_000L, decision.AvailableBytes);
        Assert.Equal(1_800_000_000L, decision.UsableBytes);
        Assert.Equal(StubDeviceProfile.Android6Gb, decision.TotalMemoryBytes);
        Assert.Equal(EdgeMemoryBudgetKind.SystemWide, decision.BudgetKind);
        Assert.NotEmpty(decision.Explanation);
        Assert.Contains("engineering estimate", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverrideHookReplacesTheWholeComputation()
    {
        var replacement = new ChatMemoryDecision(
            ChatMemoryVerdict.Allowed, 8192, 0, 0, 0, 0, 0, null, null, null,
            EdgeMemoryBudgetKind.PerProcess, false, "whoever measured the device has the last word");

        var decision = Resolve(
            Llama,
            4096,
            1L,
            StubDeviceProfile.Android(StubDeviceProfile.Android4Gb),
            new ChatMemoryBudgetOptions { Override = _ => replacement });

        Assert.Same(replacement, decision);
    }

    [Fact]
    public void AnExplicitlyLowerRequestStartsAtTheFirstRungAtOrBelowIt()
    {
        var decision = Resolve(Llama, 2048, 3_000_000_000L, StubDeviceProfile.Apple(StubDeviceProfile.Apple8Gb));

        Assert.Equal(ChatMemoryVerdict.Allowed, decision.Verdict);
        Assert.Equal(2048, decision.ContextTokens);
    }

    [Fact]
    public void AnOffLadderRequestFallsToTheRungBelowItRatherThanToNothing()
    {
        var decision = Resolve(Llama, 2500, 3_000_000_000L, StubDeviceProfile.Apple(StubDeviceProfile.Apple8Gb));

        Assert.Equal(ChatMemoryVerdict.AllowedReduced, decision.Verdict);
        Assert.Equal(2048, decision.ContextTokens);
    }
}
