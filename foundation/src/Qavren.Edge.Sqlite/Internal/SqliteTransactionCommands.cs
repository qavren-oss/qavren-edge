using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Internal;

/// <summary>
/// Commands that run inside a transaction the CALLER owns: create the command on the connection,
/// bind the transaction (SQLite refuses a command on a connection with an open transaction unless
/// the command carries it), set the text, add the parameters, run it, dispose it.
/// </summary>
/// <remarks>
/// Compiled into every package that needs it through a linked <c>Compile Include</c>, never shared
/// through <c>InternalsVisibleTo</c>: the packages are separate on nuget.org, and a grant would make
/// this type a binary contract between them. The public no-transaction equivalents are
/// <see cref="SqliteConnectionExtensions"/>.
/// </remarks>
internal static class SqliteTransactionCommands
{
    /// <summary>
    /// A command bound to <paramref name="transaction"/>. The caller owns it and must dispose it;
    /// use this when one command is re-bound and re-run, so its statement is prepared once.
    /// </summary>
    public static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<SqliteParameter> parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>Runs a statement that returns no rows, and gives back the rows it changed.</summary>
    public static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        CancellationToken cancellationToken)
    {
        var command = Command(connection, transaction, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a query and gives back its first column of its first row, as it came off the reader.</summary>
    public static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        CancellationToken cancellationToken)
    {
        var command = Command(connection, transaction, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a query and gives back its first column of its first row as <typeparamref name="T"/>;
    /// no row, or a NULL, is <c>default</c>.
    /// </summary>
    public static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<SqliteParameter> parameters,
        CancellationToken cancellationToken)
        where T : struct
    {
        var value = await ScalarAsync(connection, transaction, sql, parameters, cancellationToken)
            .ConfigureAwait(false);
        return value is null or DBNull
            ? default
            : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}
