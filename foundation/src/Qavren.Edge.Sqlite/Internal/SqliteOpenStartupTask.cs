using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Internal;

/// <summary>
/// Startup task order 10: open once, apply the creation-time pragmas, verify the key by reading
/// <c>sqlite_master</c>, and capture the info for diagnostics.
/// </summary>
public sealed class SqliteOpenStartupTask(
    EdgeDatabase database,
    ISqliteNativeProvider native,
    ILogger<SqliteOpenStartupTask> logger) : IEdgeStartupTask
{
    // CA1848: cached delegate; the plan's LogInformation carries no explicit event id, so this
    // keeps id 0 with a name, exactly what the inline call would have emitted.
    private static readonly Action<ILogger, string, string, Exception?> s_databaseOpened =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(0, "DatabaseOpened"),
            "Opened database {Database} at {Path}.");

    /// <inheritdoc />
    public int Order => EdgeStartupOrder.DatabaseOpen;

    /// <summary>
    /// Populated once the database has been opened. Null before startup task 10 runs. Read off the
    /// connection this task already holds, never through <see cref="EdgeDatabase.GetInfoAsync"/>:
    /// that awaits <c>EnsureStartedAsync</c> and would re-enter startup from inside startup.
    /// <c>UserVersion</c> is therefore the pre-migration value; the migrator runs at order 100.
    /// </summary>
    public SqliteDatabaseInfo? Info { get; private set; }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _ = native;   // ordering dependency: startup task 0 must have installed the provider first

        var connection = await database.OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await database.ApplyCreationPragmasAsync(connection, cancellationToken).ConfigureAwait(false);

            // Forces decryption on a cipher database, so a wrong key fails here rather than later.
            _ = await connection
                .ScalarAsync<long>("SELECT count(*) FROM sqlite_master", cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            Info = await database.ReadInfoAsync(connection, cancellationToken).ConfigureAwait(false);

            s_databaseOpened(logger, database.Name, database.Path, null);
        }
    }
}
