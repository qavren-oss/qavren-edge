using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Qavren.Edge.Lifecycle;

/// <summary>
/// Default <see cref="IEdgeLifecycle"/>. Observers run sequentially in registration order,
/// each inside its own try/catch, so a throwing observer never reaches the raiser and never
/// stops the ones behind it.
/// </summary>
public sealed class EdgeLifecycleHub : IEdgeLifecycle
{
    private readonly IReadOnlyList<IEdgeLifecycleObserver> _observers;
    private readonly ILogger<EdgeLifecycleHub> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private readonly LinkedList<EdgeLifecycleRecord> _history = new();

    // CA1848: the plan's inline LogDebug/LogError calls are errors under this repo's
    // TreatWarningsAsErrors + latest-recommended analysis level. LoggerMessage.Define keeps the
    // plan's exact event ids, levels and message templates while staying allocation-free and AOT-safe.
    private static readonly Action<ILogger, EdgeLifecycleEventKind, EdgeMemoryPressure?, Exception?> s_raised =
        LoggerMessage.Define<EdgeLifecycleEventKind, EdgeMemoryPressure?>(
            LogLevel.Debug,
            EdgeEventIds.LifecycleRaised,
            "Lifecycle {Kind} raised ({Level}).");

    private static readonly Action<ILogger, string?, EdgeLifecycleEventKind, Exception?> s_observerFailed =
        LoggerMessage.Define<string?, EdgeLifecycleEventKind>(
            LogLevel.Error,
            EdgeEventIds.LifecycleObserverFailed,
            "Lifecycle observer {Observer} threw handling {Kind}.");

    /// <summary>Creates the hub over the DI-registered observers.</summary>
    public EdgeLifecycleHub(
        IEnumerable<IEdgeLifecycleObserver> observers,
        IOptions<EdgeOptions> options,
        ILogger<EdgeLifecycleHub> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(options);

        _observers = [.. observers];
        _logger = logger;
        _timeProvider = timeProvider;
        _capacity = Math.Max(1, options.Value.LifecycleHistoryCapacity);
    }

    /// <inheritdoc />
    public IReadOnlyList<EdgeLifecycleRecord> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    /// <inheritdoc />
    public Task RaiseSleepingAsync(CancellationToken cancellationToken = default)
        => RaiseAsync(EdgeLifecycleEventKind.Sleeping, null, static (o, _, ct) => o.OnSleepingAsync(ct), cancellationToken);

    /// <inheritdoc />
    public Task RaiseResumedAsync(CancellationToken cancellationToken = default)
        => RaiseAsync(EdgeLifecycleEventKind.Resumed, null, static (o, _, ct) => o.OnResumedAsync(ct), cancellationToken);

    /// <inheritdoc />
    public Task RaiseMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken = default)
        => RaiseAsync(EdgeLifecycleEventKind.MemoryPressure, level, static (o, l, ct) => o.OnMemoryPressureAsync(l!.Value, ct), cancellationToken);

    /// <inheritdoc />
    public Task RaiseStoppingAsync(CancellationToken cancellationToken = default)
        => RaiseAsync(EdgeLifecycleEventKind.Stopping, null, static (o, _, ct) => o.OnStoppingAsync(ct), cancellationToken);

    private async Task RaiseAsync(
        EdgeLifecycleEventKind kind,
        EdgeMemoryPressure? level,
        Func<IEdgeLifecycleObserver, EdgeMemoryPressure?, CancellationToken, Task> invoke,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        var failures = 0;

        s_raised(_logger, kind, level, null);

        foreach (var observer in _observers)
        {
            try
            {
                await invoke(observer, level, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures++;
                s_observerFailed(_logger, observer.GetType().FullName, kind, ex);
            }
        }

        var record = new EdgeLifecycleRecord(
            _timeProvider.GetUtcNow(),
            kind,
            level,
            _timeProvider.GetElapsedTime(started),
            failures);

        lock (_gate)
        {
            _history.AddFirst(record);
            while (_history.Count > _capacity)
            {
                _history.RemoveLast();
            }
        }
    }
}
