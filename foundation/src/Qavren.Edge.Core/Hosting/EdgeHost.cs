using Microsoft.Extensions.Logging;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Hosting;

/// <summary>Default <see cref="IEdgeHost"/>: one-shot, ordered, fault-latching startup.</summary>
public sealed class EdgeHost : IEdgeHost, IDisposable
{
    private readonly IReadOnlyList<IEdgeStartupTask> _tasks;
    private readonly IEdgeLifecycle _lifecycle;
    private readonly ILogger<EdgeHost> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _gate = new();
    private readonly List<EdgeStartupTaskReport> _reports = [];
    private bool _startRequested;

    // CA1848: the plan's inline LogInformation/LogDebug/LogError calls are errors under this
    // repo's TreatWarningsAsErrors + latest-recommended analysis level. LoggerMessage.Define keeps
    // the plan's exact event ids, levels and message templates while staying allocation-free and
    // AOT-safe - the same accommodation Task 3.1 made in EdgeLifecycleHub.
    private static readonly Action<ILogger, int, Exception?> s_startupBegan =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            EdgeEventIds.StartupBegan,
            "Qavren.Edge startup began with {Count} tasks.");

    private static readonly Action<ILogger, string, TimeSpan, Exception?> s_startupTaskCompleted =
        LoggerMessage.Define<string, TimeSpan>(
            LogLevel.Debug,
            EdgeEventIds.StartupTaskCompleted,
            "Startup task {Task} completed in {Elapsed}.");

    private static readonly Action<ILogger, string, Exception?> s_startupFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            EdgeEventIds.StartupFailed,
            "Startup task {Task} failed; Qavren.Edge is faulted.");

    private static readonly Action<ILogger, Exception?> s_startupCompleted =
        LoggerMessage.Define(
            LogLevel.Information,
            EdgeEventIds.StartupCompleted,
            "Qavren.Edge startup completed.");

    /// <summary>Creates the host over the DI-registered startup tasks.</summary>
    public EdgeHost(
        IEnumerable<IEdgeStartupTask> tasks,
        IEdgeLifecycle lifecycle,
        ILogger<EdgeHost> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        // Stable sort: OrderBy is documented stable, so ties keep DI registration order.
        _tasks = [.. tasks.OrderBy(t => t.Order)];
        _lifecycle = lifecycle;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task Started => _started.Task;

    /// <summary>Startup task outcomes, for <see cref="IEdgeDiagnostics"/>.</summary>
    public IReadOnlyList<EdgeStartupTaskReport> StartupReports
    {
        get
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_startRequested)
            {
                return;
            }

            _startRequested = true;
        }

        _ = Task.Run(RunAsync);
    }

    /// <inheritdoc />
    public async ValueTask EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        Start();
        await Started.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
        => await _lifecycle.RaiseStoppingAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Nothing owned yet; present so container disposal has a hook when components register one.
    /// </summary>
    public void Dispose()
    {
        // Intentionally empty. See the summary above.
    }

    private async Task RunAsync()
    {
        s_startupBegan(_logger, _tasks.Count, null);

        foreach (var task in _tasks)
        {
            var name = task.GetType().FullName ?? task.GetType().Name;
            var started = _timeProvider.GetTimestamp();
            try
            {
                await task.RunAsync(CancellationToken.None).ConfigureAwait(false);
                var elapsed = _timeProvider.GetElapsedTime(started);
                Record(new EdgeStartupTaskReport(name, task.Order, elapsed, null));
                s_startupTaskCompleted(_logger, name, elapsed, null);
            }
            // Deliberately catches everything: the whole point is to latch any failure onto Started.
            catch (Exception ex)
            {
                Record(new EdgeStartupTaskReport(name, task.Order, _timeProvider.GetElapsedTime(started), ex.ToString()));
                s_startupFailed(_logger, name, ex);
                _started.TrySetException(ex);
                return;
            }
        }

        s_startupCompleted(_logger, null);
        _started.TrySetResult();
    }

    private void Record(EdgeStartupTaskReport report)
    {
        lock (_gate)
        {
            _reports.Add(report);
        }
    }
}
