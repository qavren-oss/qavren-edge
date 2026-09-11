namespace Qavren.Edge.Lifecycle;

public enum EdgeMemoryPressure
{
    Low,
    Moderate,
    Critical,
}

public enum EdgeLifecycleEventKind
{
    Sleeping,
    Resumed,
    MemoryPressure,
    Stopping,
}

/// <summary>One entry in the hub's rolling history, surfaced by diagnostics.</summary>
public sealed record EdgeLifecycleRecord(
    DateTimeOffset Timestamp,
    EdgeLifecycleEventKind Kind,
    EdgeMemoryPressure? Level,
    TimeSpan Duration,
    int ObserverFailures);

/// <summary>
/// A hub, not a platform abstraction: anything may raise, anything may observe.
/// Raise methods complete when every observer has finished, so a platform bridge can
/// await a WAL checkpoint before the OS suspends the app.
/// </summary>
public interface IEdgeLifecycle
{
    Task RaiseSleepingAsync(CancellationToken cancellationToken = default);

    Task RaiseResumedAsync(CancellationToken cancellationToken = default);

    Task RaiseMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken = default);

    Task RaiseStoppingAsync(CancellationToken cancellationToken = default);

    /// <summary>Most recent first, capped by <see cref="EdgeOptions.LifecycleHistoryCapacity"/>.</summary>
    IReadOnlyList<EdgeLifecycleRecord> RecentEvents { get; }
}

/// <summary>Resolved from DI; observers run in registration order, each inside its own try/catch.</summary>
public interface IEdgeLifecycleObserver
{
    Task OnSleepingAsync(CancellationToken cancellationToken);

    Task OnResumedAsync(CancellationToken cancellationToken);

    Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken);

    Task OnStoppingAsync(CancellationToken cancellationToken);
}

/// <summary>No-op base so an observer only overrides the events it cares about.</summary>
public abstract class EdgeLifecycleObserver : IEdgeLifecycleObserver
{
    public virtual Task OnSleepingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task OnResumedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task OnStoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
