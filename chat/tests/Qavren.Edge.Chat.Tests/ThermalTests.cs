using System.Diagnostics;
using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec sections 10(b) and 10(f): the pre-turn refusal, the mid-decode sampling, pacing rather than
/// stopping, and the <c>Unknown</c> arm the enum's numbering would otherwise decide by accident.
/// </summary>
public class ThermalTests
{
    private static ChatMessage[] Ask() => [new ChatMessage(ChatRole.User, "hi")];

    private static string[] Script(int tokens) => [.. Enumerable.Range(0, tokens).Select(i => $"t{i} ")];

    private static EdgeResourceSnapshot Snapshot(EdgeThermalState thermal, float? headroom = null) =>
        new(10_000_000_000L, IsLowMemory: false, thermal, headroom, IsLowPowerMode: false, LastPressure: null);

    /// <summary>Flips the monitor at a token index; advances the clock so the loop samples it.</summary>
    private static void FlipAt(ClientHarness harness, int index, EdgeThermalState thermal, bool advanceClock = true, float? headroom = null)
    {
        harness.Session.OnGeneratorCreated = generator => generator.BeforeGenerate = (_, i) =>
        {
            if (i == index)
            {
                harness.Monitor.SetSnapshot(Snapshot(thermal, headroom));
                if (advanceClock)
                {
                    harness.Clock.Advance(TimeSpan.FromSeconds(1));
                }
            }
        };
    }

    [Fact]
    public async Task ThermalIsSampledMidDecodeAtTheOneSecondBoundaryAndNotBefore()
    {
        using var harness = ClientHarness.Build(
            o => o.Thermal.ThrottledTokensPerSecond = 100_000,
            script: Script(8));

        // Serious at token 2 with the clock frozen: inside the window, so nothing changes.
        FlipAt(harness, 2, EdgeThermalState.Serious, advanceClock: false);
        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(response.GetTurnStatus()!.ThermalThrottled);
        Assert.False(harness.Logger.Logged(EdgeChatEventIds.ThermalThrottled));

        // Serious at token 2 with the clock past the boundary: sampled, and paced from there.
        FlipAt(harness, 2, EdgeThermalState.Serious);
        response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var status = response.GetTurnStatus()!;
        Assert.True(status.ThermalThrottled);
        Assert.Equal(EdgeChatStopReason.Completed, status.StopReason);
        Assert.Equal(8, status.GeneratedTokens);
        Assert.Equal(EdgeThermalState.Serious, status.Thermal);
        Assert.Single(harness.Logger.For(EdgeChatEventIds.ThermalThrottled));
        Assert.Equal(1, harness.Client.Statistics.ThermalThrottleEvents);
    }

    [Fact]
    public async Task SeriousPacesRatherThanStops()
    {
        // 20 tok/s from the first token: five tokens are four intervals of 50 ms at least.
        using var harness = ClientHarness.Build(
            o => o.Thermal.ThrottledTokensPerSecond = 20,
            script: Script(5),
            thermal: EdgeThermalState.Serious);

        var stopwatch = Stopwatch.StartNew();
        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        stopwatch.Stop();

        var status = response.GetTurnStatus()!;
        Assert.Equal(EdgeChatStopReason.Completed, status.StopReason);
        Assert.Equal(5, status.GeneratedTokens);
        Assert.True(status.ThermalThrottled);
        Assert.True(stopwatch.ElapsedMilliseconds >= 150, $"Paced decode took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task CriticalMidDecodeCompletesTheStreamWithStopReasonThermalAndNeverThrows()
    {
        // The flip lands while token 2 is being produced; the sample at the top of the next
        // iteration sees it, so exactly three tokens were produced and delivered.
        using var harness = ClientHarness.Build(script: Script(8));
        FlipAt(harness, 2, EdgeThermalState.Critical);

        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var status = response.GetTurnStatus()!;
        Assert.Equal(EdgeChatStopReason.Thermal, status.StopReason);
        Assert.Equal(3, status.GeneratedTokens);
        Assert.Equal("t0 t1 t2 ", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.True(harness.Logger.Logged(EdgeChatEventIds.ThermalAborted));
        Assert.True(harness.Session.LastGenerator.Terminated);
        Assert.Equal(1, harness.Client.Statistics.ThermalAbortEvents);
        Assert.Equal(1, harness.Client.Statistics.TerminationEvents);
    }

    [Fact]
    public async Task AtOrAboveAbortAtBeforeATurnIs7106BeforeAnythingIsAllocatedOrTheGateIsEntered()
    {
        using var harness = ClientHarness.Build(thermal: EdgeThermalState.Critical);

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatThermalAbort, exception.Code);
        Assert.Equal(EdgeThermalState.Critical, exception.Thermal);

        // The negative half: no generator, no AppendTokens, no gate entry, no lease.
        Assert.Empty(harness.Session.Generators);
        Assert.Equal(0, harness.Client.GateEntries);
        Assert.Equal(0, harness.Host.Acquires);
        Assert.Equal(1, harness.Client.Statistics.RejectedTurns);
    }

    [Fact]
    public async Task UnknownNeitherThrottlesNorAbortsAndIsPublishedVerbatim()
    {
        using var harness = ClientHarness.Build(script: Script(4), thermal: EdgeThermalState.Unknown);
        FlipAt(harness, 1, EdgeThermalState.Unknown);

        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var status = response.GetTurnStatus()!;
        Assert.Equal(EdgeChatStopReason.Completed, status.StopReason);
        Assert.False(status.ThermalThrottled);
        Assert.Equal(EdgeThermalState.Unknown, status.Thermal);
        Assert.False(harness.Logger.Logged(EdgeChatEventIds.ThermalThrottled));
        Assert.False(harness.Logger.Logged(EdgeChatEventIds.ThermalAborted));
    }

    [Fact]
    public async Task RefuseWhenThermalUnknownInvertsTheDefault()
    {
        using var harness = ClientHarness.Build(o => o.Thermal.RefuseWhenThermalUnknown = true, thermal: EdgeThermalState.Unknown);

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatThermalAbort, exception.Code);
        Assert.Equal(EdgeThermalState.Unknown, exception.Thermal);
        Assert.Contains("RefuseWhenThermalUnknown", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThermalHeadroomOfNaNNeverThrottles()
    {
        using var harness = ClientHarness.Build(script: Script(4), thermal: EdgeThermalState.Nominal);
        FlipAt(harness, 1, EdgeThermalState.Nominal, headroom: float.NaN);

        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(response.GetTurnStatus()!.ThermalThrottled);
    }

    [Fact]
    public async Task AThermalHeadroomAtTheThresholdThrottlesEvenWhenTheStateIsNominal()
    {
        using var harness = ClientHarness.Build(o => o.Thermal.ThrottledTokensPerSecond = 100_000, script: Script(4));
        FlipAt(harness, 1, EdgeThermalState.Nominal, headroom: 1.0f);

        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(response.GetTurnStatus()!.ThermalThrottled);
        Assert.Equal(EdgeChatStopReason.Completed, response.GetTurnStatus()!.StopReason);
    }

    [Fact]
    public async Task LowPowerModeRefusesOnlyWhenAsked()
    {
        using var permissive = ClientHarness.Build();
        permissive.Monitor.SetSnapshot(new EdgeResourceSnapshot(10_000_000_000L, false, EdgeThermalState.Nominal, null, IsLowPowerMode: true, null));
        var response = await permissive.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("Hello world.", response.Text);

        using var strict = ClientHarness.Build(o => o.Thermal.RefuseNewTurnsInLowPowerMode = true);
        strict.Monitor.SetSnapshot(new EdgeResourceSnapshot(10_000_000_000L, false, EdgeThermalState.Nominal, null, IsLowPowerMode: true, null));
        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => strict.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal(EdgeErrorCode.ChatBusy, exception.Code);
    }
}
