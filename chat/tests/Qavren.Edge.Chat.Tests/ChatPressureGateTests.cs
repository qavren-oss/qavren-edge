using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// When a memory-pressure pause ends. No platform sends "the pressure is over", and the one event
/// that used to end the pause - <c>Resumed</c> - never follows a warning received in the foreground.
/// </summary>
public class ChatPressureGateTests
{
    private sealed record Harness(ChatPressureGate Gate, FixedTimeProvider Clock, StubResourceMonitor Monitor);

    private static Harness Build(bool? isLowMemory = null)
    {
        var clock = new FixedTimeProvider();
        var monitor = new StubResourceMonitor(4_000_000_000L, isLowMemory: isLowMemory);
        return new Harness(new ChatPressureGate(monitor, clock), clock, monitor);
    }

    [Fact]
    public void NothingIsPausedUntilPressureArrives()
    {
        var harness = Build();

        Assert.False(harness.Gate.IsPaused);
        Assert.False(harness.Gate.IsRelieved);
    }

    [Fact]
    public void APressureEventPausesForTheWholeCooldownWhateverTheDeviceSays()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ChatPressureGate.Cooldown);

        var harness = Build(isLowMemory: false);
        harness.Gate.OnPressure();
        harness.Clock.Advance(ChatPressureGate.Cooldown - TimeSpan.FromMilliseconds(1));

        Assert.True(harness.Gate.IsPaused);
        Assert.False(harness.Gate.IsRelieved);

        // The cool-down is a floor, not a poll: the device is not even asked until it has passed.
        Assert.Equal(0, harness.Monitor.Reads);
    }

    [Fact]
    public void ThePauseEndsOnItsOwnOnceTheCooldownHasPassedAndTheDeviceIsNotLow()
    {
        var harness = Build();
        harness.Gate.OnPressure();
        harness.Clock.Advance(ChatPressureGate.Cooldown);

        Assert.False(harness.Gate.IsPaused);
        Assert.True(harness.Gate.IsRelieved);
    }

    [Fact]
    public void ADeviceThatStillReportsLowMemoryStaysPausedHoweverLongItHasBeen()
    {
        var harness = Build(isLowMemory: true);
        harness.Gate.OnPressure();
        harness.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(harness.Gate.IsPaused);
        Assert.False(harness.Gate.IsRelieved);

        harness.Monitor.SetSnapshot(
            new EdgeResourceSnapshot(4_000_000_000L, false, EdgeThermalState.Nominal, null, null, null));

        Assert.False(harness.Gate.IsPaused);
        Assert.True(harness.Gate.IsRelieved);
    }

    [Fact]
    public void ASecondPressureEventRestartsTheCooldown()
    {
        var harness = Build();
        harness.Gate.OnPressure();
        harness.Clock.Advance(TimeSpan.FromSeconds(20));
        harness.Gate.OnPressure();
        harness.Clock.Advance(TimeSpan.FromSeconds(20));

        Assert.True(harness.Gate.IsPaused);

        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        Assert.False(harness.Gate.IsPaused);
    }

    [Fact]
    public void PressureAfterReliefPausesAgainAndIsNoLongerRelieved()
    {
        var harness = Build();
        harness.Gate.OnPressure();
        harness.Clock.Advance(ChatPressureGate.Cooldown);
        Assert.True(harness.Gate.IsRelieved);

        harness.Gate.OnPressure();

        Assert.True(harness.Gate.IsPaused);
        Assert.False(harness.Gate.IsRelieved);
    }

    [Fact]
    public void ResumedClearsThePauseAndTheRecordThatThereWasOne()
    {
        var harness = Build(isLowMemory: true);
        harness.Gate.OnPressure();

        harness.Gate.Clear();

        Assert.False(harness.Gate.IsPaused);

        // Not "relieved": that is what lets the load path override the monitor's critical latch,
        // and after a Resumed the latch has been cleared by the event itself.
        Assert.False(harness.Gate.IsRelieved);
    }
}
