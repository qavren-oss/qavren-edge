using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite.Native;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

/// <summary>
/// <c>DatabaseName = ":memory:"</c>. SQLite gives every connection that opens <c>:memory:</c> a
/// database of its own, and this package opens one connection per call - so the migrator's tables
/// were in a database nobody could reach again. Each test here is one way of asking whether two
/// connections are looking at the same thing.
/// </summary>
public class InMemoryDatabaseTests
{
    private sealed class CreateNotes : IEdgeMigration
    {
        public int Version => 1;

        public string Name => "create notes";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
            => connection.ExecuteAsync(
                "CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT NOT NULL)",
                cancellationToken: cancellationToken);
    }

    private static Task<EdgeTestHost> StartAsync(CancellationToken cancellationToken) =>
        EdgeTestHost.StartAsync(
            edge => edge
                .AddSqlite(o => o.DatabaseName = ":memory:")
                .AddMigration<CreateNotes>()
                .UseSqliteNative(),
            cancellationToken);

    private static async Task<long> CountNotesAsync(IEdgeDatabase database, CancellationToken cancellationToken)
    {
        var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection
                .ScalarAsync<long>("SELECT count(*) FROM notes", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task InsertNoteAsync(IEdgeDatabase database, string title, CancellationToken cancellationToken)
    {
        var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(
                "INSERT INTO notes(title) VALUES ($title)",
                [new SqliteParameter("$title", title)],
                cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WhatTheMigratorCreatedIsThereForEveryConnectionOpenedAfterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await StartAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);

        // Three opens, three closes. The table came from the migrator's connection and the row from
        // the first of these; "no such table: notes" is what all of it used to end in.
        await InsertNoteAsync(host.Database, "first", ct);
        await InsertNoteAsync(host.Database, "second", ct);

        Assert.Equal(2, await CountNotesAsync(host.Database, ct));
        Assert.Equal(1, (await host.Database.GetInfoAsync(ct)).UserVersion);
    }

    [Fact]
    public async Task TwoConnectionsOpenAtTheSameTimeAreOnTheSameDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await StartAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);

        var writer = await host.Database.OpenConnectionAsync(ct);
        await using var writerScope = writer.ConfigureAwait(false);
        var reader = await host.Database.OpenConnectionAsync(ct);
        await using var readerScope = reader.ConfigureAwait(false);

        await writer.ExecuteAsync("INSERT INTO notes(title) VALUES ('shared')", cancellationToken: ct);

        Assert.Equal("shared", await reader.ScalarAsync<string>("SELECT title FROM notes", cancellationToken: ct));
    }

    [Fact]
    public async Task ATransactionCommitsWhereTheNextConnectionSeesItAndARollbackDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await StartAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);

        await host.Database.ExecuteInTransactionAsync(
            async (connection, _, token) =>
            {
                await connection
                    .ExecuteAsync("INSERT INTO notes(title) VALUES ('kept')", cancellationToken: token)
                    .ConfigureAwait(false);
                return true;
            },
            ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Database.ExecuteInTransactionAsync<bool>(
            async (connection, _, token) =>
            {
                await connection
                    .ExecuteAsync("INSERT INTO notes(title) VALUES ('dropped')", cancellationToken: token)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("rolled back");
            },
            ct));

        Assert.Equal(1, await CountNotesAsync(host.Database, ct));
    }

    [Fact]
    public async Task AReaderWaitsForAWriterInsteadOfFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await StartAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);

        var writer = await host.Database.OpenConnectionAsync(ct);
        await using var writerScope = writer.ConfigureAwait(false);
        var transaction = await writer.BeginTransactionAsync(ct);
        await using var transactionScope = transaction.ConfigureAwait(false);
        await writer.ExecuteAsync("INSERT INTO notes(title) VALUES ('pending')", cancellationToken: ct);

        // There is no WAL in memory, so a reader cannot be handed the last committed snapshot while
        // somebody writes: it waits, on BusyTimeout like a file database in a rollback journal mode,
        // and then reads what the writer committed.
        var read = Task.Run(() => CountNotesAsync(host.Database, ct), ct);
        await Task.Delay(250, ct);
        Assert.False(read.IsCompleted);

        await transaction.CommitAsync(ct);

        Assert.Equal(1, await read.WaitAsync(TimeSpan.FromSeconds(30), ct));
    }

    [Fact]
    public async Task ClearingThePoolsDoesNotEmptyAnInMemoryDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await StartAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);

        await InsertNoteAsync(host.Database, "survivor", ct);

        // What the lifecycle observer does under critical memory pressure and on stop, and what
        // every test host does on dispose. No connection of the app's is open at this point, and an
        // in-memory database lives exactly as long as some connection to it does.
        SqliteConnection.ClearAllPools();
        await host.Lifecycle.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, ct);

        Assert.Equal(1, await CountNotesAsync(host.Database, ct));
    }

    [Fact]
    public async Task TwoInMemoryRegistrationsAreTwoDatabases()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(
            edge => edge
                .AddSqlite(o => o.DatabaseName = ":memory:")
                .AddMigration<CreateNotes>()
                .AddSqlite("other", o => o.DatabaseName = ":memory:")
                .UseSqliteNative(),
            ct);
        await using var hostScope = host.ConfigureAwait(false);

        var other = await host.Services.GetRequiredKeyedService<IEdgeDatabase>("other").OpenConnectionAsync(ct);
        await using var otherScope = other.ConfigureAwait(false);

        Assert.Equal(0, await CountNotesAsync(host.Database, ct));
        Assert.Equal(
            0,
            await other.ScalarAsync<long>(
                "SELECT count(*) FROM sqlite_master WHERE name = 'notes'", cancellationToken: ct));
    }

    [Fact]
    public async Task TwoHostsInOneProcessDoNotShareAnInMemoryDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await StartAsync(ct);
        await using var firstScope = first.ConfigureAwait(false);
        var second = await StartAsync(ct);
        await using var secondScope = second.ConfigureAwait(false);

        await InsertNoteAsync(first.Database, "only in the first", ct);

        Assert.Equal(1, await CountNotesAsync(first.Database, ct));
        Assert.Equal(0, await CountNotesAsync(second.Database, ct));
    }

    [Fact]
    public async Task TheDatabaseGoesWhenItsHostDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        string connectionString;

        var host = await StartAsync(ct);
        await using (host.ConfigureAwait(false))
        {
            await InsertNoteAsync(host.Database, "gone with the host", ct);

            var connection = await host.Database.OpenConnectionAsync(ct);
            await using (connection.ConfigureAwait(false))
            {
                connectionString = connection.ConnectionString;
            }
        }

        // The same connection string, after the host that owned the database has been disposed: a
        // new, empty database, not the old one kept alive by something nobody can reach.
        var late = new SqliteConnection(connectionString);
        await using var lateScope = late.ConfigureAwait(false);
        await late.OpenAsync(ct);

        Assert.Equal(
            0,
            await late.ScalarAsync<long>(
                "SELECT count(*) FROM sqlite_master WHERE name = 'notes'", cancellationToken: ct));
    }

    [Fact]
    public async Task ItStillSaysItIsInMemoryAndTheFileOnlyOperationsStayHarmless()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await StartAsync(ct);
        await using var hostScope = host.ConfigureAwait(false);

        Assert.Equal(":memory:", host.Database.Path);
        Assert.False(host.Database.IsEncrypted);

        var info = await host.Database.GetInfoAsync(ct);
        Assert.Equal(0, info.FileSizeBytes);
        Assert.NotEqual("wal", info.JournalMode, StringComparer.OrdinalIgnoreCase);

        await host.Database.CheckpointAsync(ct);
        await host.Lifecycle.RaiseSleepingAsync(ct);
        Assert.Equal(0, host.Lifecycle.RecentEvents[0].ObserverFailures);

        Assert.True((await host.Database.CheckAsync(ct)).Ok);
    }
}
