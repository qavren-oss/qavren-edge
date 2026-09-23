using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.Sqlite;

/// <summary>
/// Small, visible-SQL helpers. Not an ORM: EF Core, sqlite-net-pcl and Dapper remain the
/// recommended object-mapping layers and all work unchanged on top of this provider.
/// </summary>
public static class SqliteConnectionExtensions
{
    /// <summary>Runs <paramref name="sql"/> as a non-query and returns the affected row count.</summary>
    /// <param name="connection">The open connection to run the command on.</param>
    /// <param name="sql">The SQL text to execute.</param>
    /// <param name="parameters">Parameters to bind, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sql"/> is null, empty, or whitespace.</exception>
    public static async Task<int> ExecuteAsync(
        this SqliteConnection connection,
        string sql,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var command = Prepare(connection, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs <paramref name="sql"/> and returns the first column of the first row, converted to <typeparamref name="T"/>.</summary>
    /// <param name="connection">The open connection to run the command on.</param>
    /// <param name="sql">The SQL text to execute.</param>
    /// <param name="parameters">Parameters to bind, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The converted scalar value, or <c>default</c> when the result is null or <c>DBNull</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sql"/> is null, empty, or whitespace.</exception>
    public static async Task<T?> ScalarAsync<T>(
        this SqliteConnection connection,
        string sql,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var command = Prepare(connection, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T), provider: null);
        }
    }

    /// <summary>Runs <paramref name="sql"/> and projects every row through <paramref name="map"/>.</summary>
    /// <param name="connection">The open connection to run the command on.</param>
    /// <param name="sql">The SQL text to execute.</param>
    /// <param name="map">Projects each read row to a <typeparamref name="T"/>.</param>
    /// <param name="parameters">Parameters to bind, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>Every row, in cursor order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sql"/> is null, empty, or whitespace.</exception>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        this SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> map,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);

        var command = Prepare(connection, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var rows = new List<T>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(map(reader));
                }

                return rows;
            }
        }
    }

    /// <summary>
    /// Binds each public instance property of <paramref name="parameters"/> as <c>$Name</c>.
    /// <c>ReadOnlyMemory&lt;float&gt;</c>, <c>Memory&lt;float&gt;</c> and <c>float[]</c> bind as a sqlite-vec float32 blob.
    /// </summary>
    [RequiresUnreferencedCode("Reflects over the properties of the supplied object. Use the SqliteParameter overload in trimmed or AOT apps.")]
    public static IReadOnlyList<SqliteParameter> ToParameters(object parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var list = new List<SqliteParameter>();
        foreach (var property in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var raw = property.GetValue(parameters);
            var value = raw switch
            {
                ReadOnlyMemory<float> memory => VecBlob.From(memory.Span),
                Memory<float> memory => VecBlob.From(memory.Span),
                float[] array => VecBlob.From(array),
                null => (object)DBNull.Value,
                _ => raw,
            };

            list.Add(new SqliteParameter("$" + property.Name, value));
        }

        return list;
    }

    private static SqliteCommand Prepare(
        SqliteConnection connection,
        string sql,
        IEnumerable<SqliteParameter>? parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }
        }

        return command;
    }
}
