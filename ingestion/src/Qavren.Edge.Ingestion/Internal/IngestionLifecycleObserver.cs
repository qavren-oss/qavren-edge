using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 12's table, minus the FTS5 merge row (plan adjustment 2). It is registered at index 0 of
/// the <c>IServiceCollection</c> so it runs BEFORE SP2's and SP1's observers: SP2's bounded FTS5
/// merge now runs against SP3's own sidecar, and <c>[Ingestion, VectorData, Sqlite]</c> is what
/// stops that merge running while SP3's runner is still writing to the table.
/// <para>
/// It never checkpoints WAL — that is SP1's observer's job, and doing it twice is two TRUNCATE
/// checkpoints on one Sleeping.
/// </para>
/// </summary>
internal sealed class IngestionLifecycleObserver : EdgeLifecycleObserver
{
    private readonly IngestionRunControl _control;
    private readonly IngestionRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public IngestionLifecycleObserver(
        IngestionRunControl control,
        IngestionRegistry registry,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(registry);

        _control = control;
        _registry = registry;
        _time = timeProvider ?? TimeProvider.System;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Qavren.Edge.Ingestion");
    }

    /// <inheritdoc />
    public override async Task OnSleepingAsync(CancellationToken cancellationToken)
    {
        _control.RequestStop("lifecycle:sleeping", fromLifecycle: true);
        await AwaitCheckpointAsync(SleepGrace, "sleeping", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task OnResumedAsync(CancellationToken cancellationToken)
    {
        // Clears the shrink and does NOT auto-restart a run: restarting is the app's decision.
        _control.ClearShrink();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
    {
        switch (level)
        {
            case EdgeMemoryPressure.Moderate:
                _control.Shrink();
                IngestionLog.EmbedBatchShrunk(_logger, 0, 0, "memory:moderate");
                break;

            case EdgeMemoryPressure.Critical:
                _control.Shrink();
                IngestionLog.EmbedBatchShrunk(_logger, 0, 0, "memory:critical");
                // fromLifecycle: this stop came from the OS, not from the caller, so spec 10.2
                // step 1 ends the run Suspended with SuspendReason "memory:critical". Inferring it
                // from a "lifecycle:" prefix on the reason would report a backgrounded,
                // memory-pressured run as cancelled by its caller.
                _control.RequestStop("memory:critical", fromLifecycle: true);
                break;

            default:
                break;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        _control.RequestStop("lifecycle:stopping", fromLifecycle: true);
        await AwaitCheckpointAsync(StopGrace, "stopping", cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan SleepGrace => Budget(o => o.SleepGraceBudget, TimeSpan.FromMilliseconds(750));

    private TimeSpan StopGrace => Budget(o => o.StopGraceBudget, TimeSpan.FromSeconds(5));

    private TimeSpan Budget(Func<IngestionOptions, TimeSpan> select, TimeSpan fallback)
    {
        var longest = TimeSpan.Zero;
        foreach (var registration in _registry.All)
        {
            var value = select(registration.Options);
            if (value > longest)
            {
                longest = value;
            }
        }

        return longest == TimeSpan.Zero ? fallback : longest;
    }

    private async Task AwaitCheckpointAsync(TimeSpan budget, string phase, CancellationToken cancellationToken)
    {
        // A missed grace window logs and returns IMMEDIATELY. SP1's AndroidLifecycleBridge and
        // AppleLifecycleBridge both raise with GetAwaiter().GetResult() on the platform callback
        // thread, against iOS's documented ~5 s window and Android's OnPause ANR path;
        // BeginBackgroundTask does not make a blocking delegate safe. The run keeps going and hits
        // its own checkpoint shortly, and the app was backgrounded anyway.
        var reached = await _control.WaitForCheckpointAsync(budget, _time, cancellationToken).ConfigureAwait(false);
        if (!reached)
        {
            IngestionLog.GraceWindowMissed(_logger, phase);
        }
    }
}
