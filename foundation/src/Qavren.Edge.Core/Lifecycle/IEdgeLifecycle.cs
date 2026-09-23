namespace Qavren.Edge.Lifecycle;

/// <summary>How urgently the host should shed memory, as reported by the platform bridge.</summary>
public enum EdgeMemoryPressure
{
    /// <summary>No action needed.</summary>
    Low,

    /// <summary>Release caches that are cheap to rebuild.</summary>
    Moderate,

    /// <summary>Release everything reclaimable; the OS may kill the process imminently.</summary>
    Critical,
}

/// <summary>Which lifecycle event an <see cref="EdgeLifecycleRecord"/> captured.</summary>
public enum EdgeLifecycleEventKind
{
    /// <summary>The app is about to be suspended; corresponds to <see cref="IEdgeLifecycle.RaiseSleepingAsync"/>.</summary>
    Sleeping,

    /// <summary>The app has come back to the foreground; corresponds to <see cref="IEdgeLifecycle.RaiseResumedAsync"/>.</summary>
    Resumed,

    /// <summary>The platform reported memory pressure; corresponds to <see cref="IEdgeLifecycle.RaiseMemoryPressureAsync"/>.</summary>
    MemoryPressure,

    /// <summary>The app is terminating; corresponds to <see cref="IEdgeLifecycle.RaiseStoppingAsync"/>.</summary>
    Stopping,
}

/// <summary>One entry in the hub's rolling history, surfaced by diagnostics.</summary>
/// <param name="Timestamp">When the event was raised.</param>
/// <param name="Kind">Which lifecycle event this is.</param>
/// <param name="Level">The reported pressure level, set only for <see cref="EdgeLifecycleEventKind.MemoryPressure"/>.</param>
/// <param name="Duration">How long every observer took to run.</param>
/// <param name="ObserverFailures">How many observers threw, each caught inside its own try/catch.</param>
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
    /// <summary>Raises <see cref="EdgeLifecycleEventKind.Sleeping"/> to every registered <see cref="IEdgeLifecycleObserver"/> and awaits them all.</summary>
    Task RaiseSleepingAsync(CancellationToken cancellationToken = default);

    /// <summary>Raises <see cref="EdgeLifecycleEventKind.Resumed"/> to every registered <see cref="IEdgeLifecycleObserver"/> and awaits them all.</summary>
    Task RaiseResumedAsync(CancellationToken cancellationToken = default);

    /// <summary>Raises <see cref="EdgeLifecycleEventKind.MemoryPressure"/> at the given <paramref name="level"/> to every registered <see cref="IEdgeLifecycleObserver"/> and awaits them all.</summary>
    Task RaiseMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken = default);

    /// <summary>Raises <see cref="EdgeLifecycleEventKind.Stopping"/> to every registered <see cref="IEdgeLifecycleObserver"/> and awaits them all.</summary>
    Task RaiseStoppingAsync(CancellationToken cancellationToken = default);

    /// <summary>Most recent first, capped by <see cref="EdgeOptions.LifecycleHistoryCapacity"/>.</summary>
    IReadOnlyList<EdgeLifecycleRecord> RecentEvents { get; }
}

/// <summary>Resolved from DI; observers run in registration order, each inside its own try/catch.</summary>
public interface IEdgeLifecycleObserver
{
    /// <summary>Called when <see cref="IEdgeLifecycle.RaiseSleepingAsync"/> is raised.</summary>
    Task OnSleepingAsync(CancellationToken cancellationToken);

    /// <summary>Called when <see cref="IEdgeLifecycle.RaiseResumedAsync"/> is raised.</summary>
    Task OnResumedAsync(CancellationToken cancellationToken);

    /// <summary>Called when <see cref="IEdgeLifecycle.RaiseMemoryPressureAsync"/> is raised.</summary>
    Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken);

    /// <summary>Called when <see cref="IEdgeLifecycle.RaiseStoppingAsync"/> is raised.</summary>
    Task OnStoppingAsync(CancellationToken cancellationToken);
}

/// <summary>No-op base so an observer only overrides the events it cares about.</summary>
public abstract class EdgeLifecycleObserver : IEdgeLifecycleObserver
{
    /// <inheritdoc/>
    public virtual Task OnSleepingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public virtual Task OnResumedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public virtual Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public virtual Task OnStoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
