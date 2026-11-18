using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx.Internal;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>Spec 6.4's nullable latch and the one-second read cache.</summary>
public class ResourceMonitorTests
{
    /// <summary>
    /// A hand-driven clock. The one-second read cache exists because Android's
    /// <c>GetThermalHeadroom</c> is rate-limited to ~1 Hz; asserting it against the wall clock would
    /// mean sleeping through the window in every run. The wall clock and the monotonic timestamp
    /// are kept apart, as a device keeps them: <see cref="Advance"/> moves both, as time passing
    /// does, and <see cref="StepWallClock"/> moves only the wall clock, as NTP or a user does.
    /// Not a <c>FakeTimeProvider</c>: it cannot step the wall clock backward on its own, and these
    /// tests assert the read cache ignores the wall clock.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by)
        {
            _now += by;
            _timestamp += by.Ticks;
        }

        public void StepWallClock(TimeSpan by) => _now += by;
    }

    [Fact]
    public void LastPressureIsNullBeforeAnythingIsReported()
    {
        var monitor = new DefaultEdgeResourceMonitor();

        Assert.Null(monitor.LastPressure);
        Assert.Null(monitor.Read().LastPressure);
    }

    [Fact]
    public void SetPressureLatchesAndNullClearsIt()
    {
        var monitor = new DefaultEdgeResourceMonitor();

        monitor.SetPressure(EdgeMemoryPressure.Moderate);
        Assert.Equal(EdgeMemoryPressure.Moderate, monitor.LastPressure);
        Assert.Equal(EdgeMemoryPressure.Moderate, monitor.Read().LastPressure);

        monitor.SetPressure(null);
        Assert.Null(monitor.LastPressure);
        Assert.Null(monitor.Read().LastPressure);
    }

    [Fact]
    public void TheDesktopReadReportsMemoryAndAnUnknownThermalState()
    {
        var monitor = new DefaultEdgeResourceMonitor();

        var snapshot = monitor.Read();

        Assert.NotNull(snapshot.AvailableMemoryBytes);
        Assert.True(snapshot.AvailableMemoryBytes > 0);
        Assert.Equal(EdgeThermalState.Unknown, snapshot.Thermal);
        Assert.Null(snapshot.ThermalHeadroom);
        Assert.Null(snapshot.IsLowPowerMode);
    }

    [Fact]
    public void ReadsAreCachedForOneSecond()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var monitor = new DefaultEdgeResourceMonitor(time);

        var first = monitor.Read();

        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Same(first, monitor.Read());

        time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.NotSame(first, monitor.Read());
    }

    [Fact]
    public void AWallClockSteppedBackDoesNotFreezeTheCachedReading()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var monitor = new DefaultEdgeResourceMonitor(time);

        var first = monitor.Read();

        // On the wall clock the window would now end an hour from here, and every read until then
        // - the thermal samples a running turn aborts on among them - would get this snapshot.
        time.StepWallClock(TimeSpan.FromHours(-1));
        time.Advance(TimeSpan.FromMilliseconds(1001));

        Assert.NotSame(first, monitor.Read());
    }

    [Fact]
    public void AWallClockSteppedForwardDoesNotCutTheCacheWindowShort()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var monitor = new DefaultEdgeResourceMonitor(time);

        var first = monitor.Read();

        // No time has passed, so Android's ~1 Hz headroom read must not be asked again.
        time.StepWallClock(TimeSpan.FromHours(1));

        Assert.Same(first, monitor.Read());
    }

    [Fact]
    public void LatchingInvalidatesTheCachedSnapshot()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var monitor = new DefaultEdgeResourceMonitor(time);

        var first = monitor.Read();
        monitor.SetPressure(EdgeMemoryPressure.Critical);

        var second = monitor.Read();

        Assert.NotSame(first, second);
        Assert.Equal(EdgeMemoryPressure.Critical, second.LastPressure);
    }

    [Fact]
    public async Task TheLifecycleObserverLatchesPressureAndClearsItOnResume()
    {
        var monitor = new DefaultEdgeResourceMonitor();

        // An empty provider: the latch is all this test is about, and the observer resolves the
        // session host lazily precisely so there is nothing to stand up when there is no session.
        var observer = new OnnxLifecycleObserver(monitor, new ServiceCollection().BuildServiceProvider());

        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, TestContext.Current.CancellationToken);
        Assert.Equal(EdgeMemoryPressure.Moderate, monitor.LastPressure);

        await observer.OnResumedAsync(TestContext.Current.CancellationToken);
        Assert.Null(monitor.LastPressure);
    }
}
