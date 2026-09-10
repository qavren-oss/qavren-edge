using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class LifecycleHubTests
{
    private sealed class Recorder(List<string> log, string name, bool throwOnSleeping = false)
        : EdgeLifecycleObserver
    {
        public override Task OnSleepingAsync(CancellationToken cancellationToken)
        {
            log.Add(name + ":sleeping");
            return throwOnSleeping
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;
        }

        public override Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
        {
            log.Add($"{name}:memory:{level}");
            return Task.CompletedTask;
        }
    }

    private static EdgeLifecycleHub Create(IEnumerable<IEdgeLifecycleObserver> observers, int capacity = 64)
        => new(
            observers,
            Options.Create(new EdgeOptions { LifecycleHistoryCapacity = capacity }),
            NullLogger<EdgeLifecycleHub>.Instance,
            TimeProvider.System);

    [Fact]
    public async Task Observers_RunInRegistrationOrder()
    {
        var log = new List<string>();
        var hub = Create([new Recorder(log, "a"), new Recorder(log, "b"), new Recorder(log, "c")]);

        await hub.RaiseSleepingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a:sleeping", "b:sleeping", "c:sleeping"], log);
    }

    [Fact]
    public async Task ThrowingObserver_IsIsolatedAndRemainingObserversStillRun()
    {
        var log = new List<string>();
        var hub = Create([new Recorder(log, "a", throwOnSleeping: true), new Recorder(log, "b")]);

        await hub.RaiseSleepingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a:sleeping", "b:sleeping"], log);
        Assert.Equal(1, hub.RecentEvents[0].ObserverFailures);
    }

    [Fact]
    public async Task MemoryPressure_PassesLevelThroughAndIsRecorded()
    {
        var log = new List<string>();
        var hub = Create([new Recorder(log, "a")]);

        await hub.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken);

        Assert.Equal(["a:memory:Critical"], log);
        var record = hub.RecentEvents[0];
        Assert.Equal(EdgeLifecycleEventKind.MemoryPressure, record.Kind);
        Assert.Equal(EdgeMemoryPressure.Critical, record.Level);
    }

    [Fact]
    public async Task History_IsMostRecentFirstAndCapped()
    {
        var hub = Create([], capacity: 2);

        await hub.RaiseSleepingAsync(TestContext.Current.CancellationToken);
        await hub.RaiseResumedAsync(TestContext.Current.CancellationToken);
        await hub.RaiseStoppingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, hub.RecentEvents.Count);
        Assert.Equal(EdgeLifecycleEventKind.Stopping, hub.RecentEvents[0].Kind);
        Assert.Equal(EdgeLifecycleEventKind.Resumed, hub.RecentEvents[1].Kind);
    }
}
