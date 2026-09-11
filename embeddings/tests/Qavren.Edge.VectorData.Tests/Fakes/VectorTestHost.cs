using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Xunit;

namespace Qavren.Edge.VectorData.Tests.Fakes;

/// <summary>
/// A fully wired Qavren.Edge host over a scratch directory, deleted on dispose. Every test in this
/// suite opens real connections over the win-x64 native, so <c>vec0</c> and FTS5 are live from the
/// first assertion rather than mocked.
/// </summary>
public sealed class VectorTestHost : IDisposable
{
    private readonly ServiceProvider _services;

    private VectorTestHost(ServiceProvider services, string root)
    {
        _services = services;
        Root = root;
    }

    /// <summary>The scratch directory holding the database file.</summary>
    public string Root { get; }

    /// <summary>The container.</summary>
    public IServiceProvider Services => _services;

    /// <summary>The unnamed database.</summary>
    public IEdgeDatabase Database => _services.GetRequiredService<IEdgeDatabase>();

    /// <summary>Starts a host with one unnamed SQLite database in a fresh scratch directory.</summary>
    /// <param name="configure">Adds further registrations to the builder.</param>
    /// <returns>The started host.</returns>
    public static async Task<VectorTestHost> StartAsync(Action<EdgeBuilder>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-vectordata-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging();

        // AddQavrenEdge registers IEdgePaths with TryAddSingleton, so this has to come first.
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));

        services.AddQavrenEdge(edge =>
        {
            edge.UseSqliteNative();
            edge.AddSqlite(o =>
            {
                o.DatabaseName = "test.db";
                o.Directory = root;
            });
            configure?.Invoke(edge);
        });

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IEdgeHost>()
            .EnsureStartedAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        return new VectorTestHost(provider, root);
    }

    /// <summary>Reads one column of one row as raw bytes, with no provider code in the path.</summary>
    /// <param name="table">The table to read.</param>
    /// <param name="column">The column to read.</param>
    /// <param name="rowId">The rowid to read.</param>
    /// <returns>The stored bytes.</returns>
    public async Task<byte[]> ReadBlobAsync(string table, string column, long rowId)
    {
        var token = TestContext.Current.CancellationToken;
        var connection = await Database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = $"SELECT \"{column}\" FROM \"{table}\" WHERE rowid = $rowid";
                command.Parameters.Add(new SqliteParameter("$rowid", rowId));

                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    Assert.True(await reader.ReadAsync(token).ConfigureAwait(false));
                    return reader.GetFieldValue<byte[]>(0);
                }
            }
        }
    }

    /// <summary>Runs one statement against the same database an app's own SQL would use.</summary>
    /// <param name="sql">The statement.</param>
    /// <returns>A task that completes when the statement has run.</returns>
    public async Task ExecuteAsync(string sql)
    {
        var token = TestContext.Current.CancellationToken;
        var connection = await Database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(sql, cancellationToken: token).ConfigureAwait(false);
        }
    }

    /// <summary>Counts the rows of one table.</summary>
    /// <param name="table">The table to count.</param>
    /// <returns>The row count.</returns>
    public async Task<long> CountAsync(string table)
    {
        var token = TestContext.Current.CancellationToken;
        var connection = await Database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection
                .ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"", cancellationToken: token)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reads every table name straight out of <c>sqlite_master</c>, filtering nothing.</summary>
    /// <returns>The table names.</returns>
    public async Task<IReadOnlyList<string>> AllTableNamesAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = await Database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name",
                reader => reader.GetString(0),
                parameters: null,
                token).ConfigureAwait(false);
        }
    }

    /// <summary>Reads every trigger name straight out of <c>sqlite_master</c>.</summary>
    /// <returns>The trigger names.</returns>
    public async Task<IReadOnlyList<string>> AllTriggerNamesAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = await Database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name",
                reader => reader.GetString(0),
                parameters: null,
                token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Synchronous on purpose. An <c>await using</c> in a test body trips CA2007, whose only fix -
    /// ConfigureAwait(false) - is what xunit's xUnit1030 forbids there, so the host is disposed
    /// with a plain <c>using</c> instead. ServiceProvider implements both interfaces.
    /// </summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _services.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A WAL file may still be mapped on Windows; a leftover temp directory is harmless.
        }
    }

    private sealed class FixedPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
