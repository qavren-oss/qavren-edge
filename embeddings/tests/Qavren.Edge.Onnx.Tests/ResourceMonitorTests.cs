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
    /// mean sleeping through the window in every run.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
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
        var observer = new OnnxLifecycleObserver(monitor);

        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, TestContext.Current.CancellationToken);
        Assert.Equal(EdgeMemoryPressure.Moderate, monitor.LastPressure);

        await observer.OnResumedAsync(TestContext.Current.CancellationToken);
        Assert.Null(monitor.LastPressure);
    }
}
