using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.VectorData.Tests.Fakes;

/// <summary>
/// The same database file as the host's, through connections that count the commands created on
/// them. Microsoft.Data.Sqlite prepares a command's statement the first time it runs and keeps it
/// for as long as the command lives, so "how many commands did that take" is "how many statements
/// were compiled" - the one cost of a write that the SQL text does not show.
/// </summary>
internal sealed class CommandCountingDatabase(IEdgeDatabase inner) : IEdgeDatabase
{
    private int _commands;

    /// <summary>How many commands have been created on this database's connections so far.</summary>
    public int CommandsCreated => Volatile.Read(ref _commands);

    public string Name => inner.Name;

    public string Path => inner.Path;

    public bool IsEncrypted => inner.IsEncrypted;

    public async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new CountingConnection(
            this,
            new SqliteConnectionStringBuilder { DataSource = inner.Path, ForeignKeys = true }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
        }
    }

    public Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        inner.GetInfoAsync(cancellationToken);

    public Task<SqliteCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        inner.CheckAsync(cancellationToken);

    public Task RekeyAsync(SqliteKey newKey, CancellationToken cancellationToken = default) =>
        inner.RekeyAsync(newKey, cancellationToken);

    public Task CheckpointAsync(CancellationToken cancellationToken = default) =>
        inner.CheckpointAsync(cancellationToken);

    private sealed class CountingConnection(CommandCountingDatabase owner, string connectionString)
        : SqliteConnection(connectionString)
    {
        public override SqliteCommand CreateCommand()
        {
            Interlocked.Increment(ref owner._commands);
            return base.CreateCommand();
        }
    }
}
