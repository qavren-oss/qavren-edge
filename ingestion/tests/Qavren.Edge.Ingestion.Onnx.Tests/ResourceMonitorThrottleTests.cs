using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Ingestion.Onnx.Tests;

/// <summary>Spec 10.2's table, row by row, against a fake <see cref="IEdgeResourceMonitor"/>.</summary>
public sealed class ResourceMonitorThrottleTests
{
    private const long Mib = 1024L * 1024;

    private static IngestionThrottleContext Context(int configured = 32) =>
        new(configured, configured, DocumentsProcessed: 0, ChunksWritten: 0, Elapsed: TimeSpan.Zero);

    private static EdgeResourceSnapshot Fine() => new(
        AvailableMemoryBytes: 1024 * Mib,
        IsLowMemory: false,
        EdgeThermalState.Nominal,
        ThermalHeadroom: null,
        IsLowPowerMode: false,
        LastPressure: null);

    /// <summary>The eight rows, plus the two "unknown is not fine" rows, plus the clamp.</summary>
    public static TheoryData<string, EdgeResourceSnapshot, bool, int, int, string?> Rows => new()
    {
        { "memory:critical", Fine() with { LastPressure = EdgeMemoryPressure.Critical }, true, 32, 0, "memory:critical" },
        { "memory:floor", Fine() with { AvailableMemoryBytes = (48 * Mib) - 1 }, true, 32, 0, "memory:floor" },
        { "thermal:critical", Fine() with { Thermal = EdgeThermalState.Critical }, true, 32, 0, "thermal:critical" },
        { "thermal:serious", Fine() with { Thermal = EdgeThermalState.Serious }, false, 16, 250, "thermal:serious" },
        { "memory:moderate", Fine() with { LastPressure = EdgeMemoryPressure.Moderate }, false, 16, 0, "memory:moderate" },
        { "memory:warn", Fine() with { AvailableMemoryBytes = (96 * Mib) - 1 }, false, 16, 0, "memory:warn" },
        { "power:low", Fine() with { IsLowPowerMode = true }, false, 4, 100, "power:low" },
        { "otherwise", Fine(), false, 32, 0, null },
        { "thermal unknown is no signal", Fine() with { Thermal = EdgeThermalState.Unknown }, false, 32, 0, null },
        { "null memory is no signal", Fine() with { AvailableMemoryBytes = null, IsLowMemory = null, IsLowPowerMode = null }, false, 32, 0, null },
        { "low pressure is no signal", Fine() with { LastPressure = EdgeMemoryPressure.Low }, false, 32, 0, null },
        { "fair thermal is no signal", Fine() with { Thermal = EdgeThermalState.Fair }, false, 32, 0, null },
        { "exactly 48 MiB is not below the floor", Fine() with { AvailableMemoryBytes = 48 * Mib }, false, 16, 0, "memory:warn" },
        { "exactly 96 MiB is not below warn", Fine() with { AvailableMemoryBytes = 96 * Mib }, false, 32, 0, null },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Each_row_of_the_table_produces_its_decision(
        string row, EdgeResourceSnapshot snapshot, bool pause, int batch, int delayMs, string? reason)
    {
        var monitor = new StubResourceMonitor { Next = snapshot };
        var throttle = new ResourceMonitorThrottle(monitor);

        var decision = throttle.Evaluate(Context());

        Assert.True(pause == decision.Pause, row);
        Assert.Equal(batch, decision.BatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(delayMs), decision.Delay);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(1, monitor.ReadCount);
    }

    [Fact]
    public void First_match_wins_a_critical_pressure_beats_a_serious_thermal_state()
    {
        var monitor = new StubResourceMonitor
        {
            Next = Fine() with { LastPressure = EdgeMemoryPressure.Critical, Thermal = EdgeThermalState.Serious },
        };

        var decision = new ResourceMonitorThrottle(monitor).Evaluate(Context());

        Assert.True(decision.Pause);
        Assert.Equal("memory:critical", decision.Reason);
    }

    [Fact]
    public void A_serious_thermal_state_beats_moderate_pressure_and_low_power()
    {
        var monitor = new StubResourceMonitor
        {
            Next = Fine() with
            {
                Thermal = EdgeThermalState.Serious,
                LastPressure = EdgeMemoryPressure.Moderate,
                IsLowPowerMode = true,
            },
        };

        var decision = new ResourceMonitorThrottle(monitor).Evaluate(Context());

        Assert.False(decision.Pause);
        Assert.Equal(16, decision.BatchSize);
        Assert.Equal("thermal:serious", decision.Reason);
    }

    [Theory]
    [InlineData(32, 4, 16)]
    [InlineData(6, 4, 4)]
    [InlineData(2, 4, 2)]
    [InlineData(32, 20, 20)]
    public void A_halving_is_clamped_to_MinBatchSize_and_never_exceeds_the_configured_batch(
        int configured, int minBatch, int expected)
    {
        var monitor = new StubResourceMonitor { Next = Fine() with { LastPressure = EdgeMemoryPressure.Moderate } };
        var throttle = new ResourceMonitorThrottle(monitor, new ResourceMonitorThrottleOptions { MinBatchSize = minBatch });

        var decision = throttle.Evaluate(Context(configured));

        Assert.Equal(expected, decision.BatchSize);
        Assert.Equal("memory:moderate", decision.Reason);
    }

    [Fact]
    public void Low_power_takes_min_of_configured_and_four()
    {
        var monitor = new StubResourceMonitor { Next = Fine() with { IsLowPowerMode = true } };
        var throttle = new ResourceMonitorThrottle(monitor);

        Assert.Equal(4, throttle.Evaluate(Context(32)).BatchSize);
        Assert.Equal(2, throttle.Evaluate(Context(2)).BatchSize);
    }

    [Fact]
    public void The_batch_is_derived_from_the_configured_size_not_the_current_one()
    {
        // The runner already applied the lifecycle shrink to CurrentBatchSize and takes the
        // smaller of the two decisions; halving the halved value would collapse to the floor.
        var monitor = new StubResourceMonitor { Next = Fine() with { LastPressure = EdgeMemoryPressure.Moderate } };
        var context = new IngestionThrottleContext(32, 8, 0, 0, TimeSpan.Zero);

        var decision = new ResourceMonitorThrottle(monitor).Evaluate(context);

        Assert.Equal(16, decision.BatchSize);
    }

    [Fact]
    public void IgnoreLowPowerMode_skips_only_the_power_row()
    {
        var monitor = new StubResourceMonitor { Next = Fine() with { IsLowPowerMode = true } };
        var throttle = new ResourceMonitorThrottle(monitor, new ResourceMonitorThrottleOptions { IgnoreLowPowerMode = true });

        var decision = throttle.Evaluate(Context());

        Assert.Equal(IngestionThrottleDecision.Proceed(32), decision);

        monitor.Next = Fine() with { IsLowPowerMode = true, LastPressure = EdgeMemoryPressure.Moderate };
        Assert.Equal("memory:moderate", throttle.Evaluate(Context()).Reason);
    }

    [Fact]
    public void IgnoreThermalState_skips_both_thermal_rows_and_never_the_memory_rows()
    {
        var monitor = new StubResourceMonitor();
        var throttle = new ResourceMonitorThrottle(monitor, new ResourceMonitorThrottleOptions { IgnoreThermalState = true });

        monitor.Next = Fine() with { Thermal = EdgeThermalState.Critical };
        Assert.Equal(IngestionThrottleDecision.Proceed(32), throttle.Evaluate(Context()));

        monitor.Next = Fine() with { Thermal = EdgeThermalState.Serious };
        Assert.Equal(IngestionThrottleDecision.Proceed(32), throttle.Evaluate(Context()));

        monitor.Next = Fine() with { Thermal = EdgeThermalState.Critical, LastPressure = EdgeMemoryPressure.Critical };
        Assert.Equal("memory:critical", throttle.Evaluate(Context()).Reason);
    }

    [Fact]
    public void The_name_is_stable_and_a_bad_floor_is_refused_at_construction()
    {
        Assert.Equal("resource-monitor", new ResourceMonitorThrottle(new StubResourceMonitor()).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourceMonitorThrottle(new StubResourceMonitor(), new ResourceMonitorThrottleOptions { MinBatchSize = 0 }));
        Assert.Throws<ArgumentNullException>(() => new ResourceMonitorThrottle(null!));
    }
}
