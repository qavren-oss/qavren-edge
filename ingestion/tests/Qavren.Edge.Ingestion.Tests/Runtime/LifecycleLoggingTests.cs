using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// What the lifecycle observer says in the log. Event 918 is "the write batch shrank", and a line
/// that says it shrank from 0 to 0 - or from 4 to 4 - is a filter on 918 lying about what happened.
/// </summary>
public sealed class LifecycleLoggingTests
{
    private static string[] Shrunk(IngestionTestHost host) =>
        [.. host.Logs.Lines.Where(l => l.StartsWith("918|", StringComparison.Ordinal))];

    private static IngestionLifecycleObserver Observer(IngestionTestHost host) =>
        host.Services.GetServices<IEdgeLifecycleObserver>().OfType<IngestionLifecycleObserver>().Single();

    [Fact]
    public async Task Memory_pressure_logs_the_batch_it_really_shrank_from_and_to()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.WriteBatchSize = 32);

        await Observer(host).OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, TestContext.Current.CancellationToken);

        var line = Assert.Single(Shrunk(host));
        Assert.Equal("918|Write batch shrunk from 32 to 16: memory:moderate.", line);
    }

    [Fact]
    public async Task Memory_pressure_at_the_floor_changes_nothing_and_logs_nothing()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.WriteBatchSize = 4);

        await Observer(host).OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, TestContext.Current.CancellationToken);
        await Observer(host).OnMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken);

        Assert.Empty(Shrunk(host));
    }

    [Fact]
    public async Task Memory_pressure_with_no_ingestion_registered_logs_no_shrink()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var observer = new IngestionLifecycleObserver(
            new IngestionRunControl(), new IngestionRegistry(), new ManualTimeProvider(), factory);

        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logs.Lines, l => l.StartsWith("918|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missed_grace_window_is_logged_as_a_warning_under_902()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var control = new IngestionRunControl();
        var time = new ManualTimeProvider();
        var observer = new IngestionLifecycleObserver(control, new IngestionRegistry(), time, factory);

        control.BeginRun();
        var sleeping = observer.OnSleepingAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await sleeping.ConfigureAwait(true);

        Assert.Contains(
            "902|The lifecycle grace window elapsed before the runner reached a committed checkpoint: sleeping.",
            logs.Lines);
    }
}
