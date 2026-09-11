using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite;

/// <summary>
/// Startup task order 100. Reads <c>PRAGMA user_version</c>, runs each pending migration in its own
/// transaction, and sets <c>user_version</c> after each. A failure stops the run and raises
/// <see cref="EdgeMigrationException"/>; migrations already applied stay applied.
/// </summary>
public sealed class EdgeMigrator(
    EdgeDatabase database,
    IReadOnlyList<IEdgeMigration> migrations,
    ILogger<EdgeMigrator> logger) : IEdgeStartupTask
{
    // CA1848: cached delegates keep the plan's event ids, levels and templates. See EdgeHost.
    private static readonly Action<ILogger, int, string, string, Exception?> s_migrationApplied =
        LoggerMessage.Define<int, string, string>(
            LogLevel.Information,
            EdgeEventIds.MigrationApplied,
            "Applied migration {Version} '{Name}' to {Database}.");

    private static readonly Action<ILogger, int, string, string, Exception?> s_migrationFailed =
        LoggerMessage.Define<int, string, string>(
            LogLevel.Error,
            EdgeEventIds.MigrationFailed,
            "Migration {Version} '{Name}' failed on {Database}.");

    /// <inheritdoc />
    public int Order => EdgeStartupOrder.Migrations;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (migrations.Count == 0)
        {
            return;
        }

        var connection = await database.OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var current = await connection
                .ScalarAsync<int>("PRAGMA user_version", cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var migration in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
            {
                var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    try
                    {
                        await migration.UpAsync(connection, cancellationToken).ConfigureAwait(false);

                        // PRAGMA takes no parameters; Version is an int we produced, so interpolation is safe.
                        await connection.ExecuteAsync(
                            "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture) + ";",
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        s_migrationApplied(logger, migration.Version, migration.Name, database.Name, null);
                    }
                    // Every failure is rewrapped as EdgeMigrationException with the original as inner.
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        s_migrationFailed(logger, migration.Version, migration.Name, database.Name, ex);
                        throw new EdgeMigrationException(migration.Version, migration.Name, ex);
                    }
                }
            }
        }
    }
}
