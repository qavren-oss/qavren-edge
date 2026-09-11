using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.VectorData.Internal;

/// <summary>
/// Index maintenance is a lifecycle hook, not an inline cost.
/// <para>
/// On <c>Sleeping</c> this runs one incremental FTS5 merge per collection that has a full-text
/// sidecar, bounded to four iterations and a two-second budget, each inside its own try/catch and
/// abandoned rather than failed when the budget expires. The full <c>'optimize'</c> form merges
/// every b-tree into one and SQLite's own docs warn it "can take a long time to run" - precisely
/// wrong to do inline on a phone, which is why only the incremental form runs and only on this
/// event. Sub-project 1's <c>SqliteLifecycleObserver</c> checkpoints WAL on the same event, and
/// registration order puts this merge <b>first</b> so its pages land in that checkpoint.
/// </para>
/// <para>
/// Every other event does nothing. Pool clearing is sub-project 1's job and it already does it.
/// </para>
/// </summary>
internal sealed class VectorDataLifecycleObserver : EdgeLifecycleObserver
{
    /// <summary>Iterations of the incremental merge per collection per sleep.</summary>
    private const int MaxIterations = 4;

    /// <summary>The <c>'merge'</c> argument: how many b-tree leaves one iteration may touch.</summary>
    private const int MergePages = 500;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static readonly Action<ILogger, string, int, Exception?> LogFtsMergeCompleted =
        LoggerMessage.Define<string, int>(
            LogLevel.Debug,
            new EventId(803, "FtsMergeCompleted"),
            "Incremental FTS5 merge on '{Table}' ran {Iterations} iteration(s).");

    private readonly IEnumerable<IEdgeDatabase> _databases;
    private readonly EdgeVectorCollectionRegistry _registry;
    private readonly ILogger<VectorDataLifecycleObserver> _logger;

    /// <summary>Creates the observer.</summary>
    /// <param name="databases">Every registered database, resolved by name against the registry.</param>
    /// <param name="registry">The collections the builder registered.</param>
    /// <param name="logger">Receives <c>FtsMergeCompleted</c> (803).</param>
    public VectorDataLifecycleObserver(
        IEnumerable<IEdgeDatabase> databases,
        EdgeVectorCollectionRegistry registry,
        ILogger<VectorDataLifecycleObserver> logger)
    {
        _databases = databases;
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task OnSleepingAsync(CancellationToken cancellationToken)
    {
        var mergeable = _registry.Registrations.Where(r => r.FullTextTable is not null).ToArray();
        if (mergeable.Length == 0)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var databases = _databases.ToArray();

        foreach (var registration in mergeable)
        {
            if (clock.Elapsed >= Budget)
            {
                return;
            }

            var database = databases.FirstOrDefault(d =>
                string.Equals(d.Name, registration.DatabaseName, StringComparison.Ordinal));
            if (database is null)
            {
                continue;
            }

            await MergeAsync(database, registration.FullTextTable!, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MergeAsync(
        IEdgeDatabase database,
        string table,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        // FTS5 wants the table's own name as the first column of the insert.
        var sql = $"INSERT INTO \"{table}\"(\"{table}\", rank) VALUES ('merge', {MergePages})";
        var completed = 0;

        try
        {
            var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                for (var i = 0; i < MaxIterations && clock.Elapsed < Budget; i++)
                {
                    try
                    {
                        await connection.ExecuteAsync(sql, parameters: null, cancellationToken).ConfigureAwait(false);
                        completed++;
                    }
                    catch (SqliteException)
                    {
                        // A merge that cannot run is not a reason to fail an OS sleep callback.
                        break;
                    }
                }
            }
        }
        catch (SqliteException)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (completed > 0)
        {
            LogFtsMergeCompleted(_logger, table, completed, null);
        }
    }
}
