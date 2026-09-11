using Microsoft.Data.Sqlite;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Sqlite.Internal;

/// <summary>Spec 8.7: checkpoint on sleep, drop pools under critical memory pressure, clear everything on stop.</summary>
public sealed class SqliteLifecycleObserver(IEnumerable<IEdgeDatabase> databases) : EdgeLifecycleObserver
{
    /// <inheritdoc />
    public override async Task OnSleepingAsync(CancellationToken cancellationToken)
    {
        foreach (var database in databases)
        {
            await database.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
    {
        if (level == EdgeMemoryPressure.Critical)
        {
            SqliteConnection.ClearAllPools();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        return Task.CompletedTask;
    }
}
