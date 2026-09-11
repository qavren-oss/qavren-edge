using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite.Native;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class MigrationTests
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

    private sealed class AddBody : IEdgeMigration
    {
        public int Version => 2;

        public string Name => "add body";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
            => connection.ExecuteAsync("ALTER TABLE notes ADD COLUMN body TEXT", cancellationToken: cancellationToken);
    }

    private sealed class Broken : IEdgeMigration
    {
        public int Version => 3;

        public string Name => "broken";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
            => connection.ExecuteAsync("CREATE TABLE notes(oops)", cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task FreshDatabase_AppliesEveryMigrationAndSetsUserVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var host = await EdgeTestHost.StartAsync(edge => edge
            .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
            .AddMigration<CreateNotes>()
            .AddMigration<AddBody>()
            .UseSqliteNative(), TestContext.Current.CancellationToken);
        await using var hostScope = host.ConfigureAwait(false);

        var info = await host.Database.GetInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, info.UserVersion);

        var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var connectionScope = connection.ConfigureAwait(false);
        var columns = await connection.QueryAsync(
            "PRAGMA table_info(notes)", r => r.GetString(1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("body", columns);
    }

    [Fact]
    public async Task Rerun_IsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        for (var run = 0; run < 2; run++)
        {
            var host = await EdgeTestHost.StartAsync(edge => edge
                .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
                .AddMigration<CreateNotes>()
                .UseSqliteNative(), TestContext.Current.CancellationToken);
            await using (host.ConfigureAwait(false))
            {
                Assert.Equal(1, (await host.Database.GetInfoAsync(TestContext.Current.CancellationToken)).UserVersion);
            }
        }
    }

    // Spec 13 lists FOUR migration cases: fresh, PARTIAL, failing mid-run, idempotent rerun.
    // This is the partial one - the path every real app takes on its second release: the file
    // already sits at user_version = 1, so migration 1 must be SKIPPED (proved by the fact that
    // re-running it would throw "table notes already exists") and only 2 and 3 may run.
    [Fact]
    public async Task PartialUpgrade_SkipsAppliedMigrationsAndRunsOnlyThePendingOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Release 1 of the app: only migration 1 exists.
        var v1 = await EdgeTestHost.StartAsync(edge => edge
            .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
            .AddMigration<CreateNotes>()
            .UseSqliteNative(), TestContext.Current.CancellationToken);
        await using (v1.ConfigureAwait(false))
        {
            Assert.Equal(1, (await v1.Database.GetInfoAsync(TestContext.Current.CancellationToken)).UserVersion);

            var seed = await v1.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using (seed.ConfigureAwait(false))
            {
                await seed.ExecuteAsync(
                    "INSERT INTO notes(id, title) VALUES (1, 'kept')",
                    cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        SqliteConnection.ClearAllPools();

        // Release 2 of the same app, against the SAME file: migrations 1, 2 and 3 are all
        // registered, but only 2 and 3 are pending.
        var ran = new List<int>();
        var v2 = await EdgeTestHost.StartAsync(edge => edge
            .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
            .AddMigration<CreateNotes>()
            .AddMigrations([new RecordingMigration(2, "add body", ran, "ALTER TABLE notes ADD COLUMN body TEXT"),
                            new RecordingMigration(3, "add tags", ran, "ALTER TABLE notes ADD COLUMN tags TEXT")])
            .UseSqliteNative(), TestContext.Current.CancellationToken);
        await using var v2Scope = v2.ConfigureAwait(false);

        // Exactly the pending set ran, in ascending order. Migration 1 never executed: had it
        // run again, its CREATE TABLE would have failed and faulted startup.
        Assert.Equal([2, 3], ran);
        Assert.Equal(3, (await v2.Database.GetInfoAsync(TestContext.Current.CancellationToken)).UserVersion);

        var connection = await v2.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var connectionScope = connection.ConfigureAwait(false);

        var columns = await connection.QueryAsync(
            "PRAGMA table_info(notes)", r => r.GetString(1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("body", columns);
        Assert.Contains("tags", columns);

        // The pre-existing row survived: a partial upgrade migrates, it does not recreate.
        Assert.Equal("kept", await connection.ScalarAsync<string>(
            "SELECT title FROM notes WHERE id = 1",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>A migration that records the fact that it ran, so the test can assert which
    /// versions the migrator chose to execute rather than only the end state.</summary>
    private sealed class RecordingMigration(int version, string name, List<int> ran, string sql) : IEdgeMigration
    {
        public int Version => version;

        public string Name => name;

        public async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            ran.Add(version);
            await connection.ExecuteAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task FailureMidRun_LeavesTheLastGoodVersionAndRaisesEdgeMigrationException()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new TestPaths(root));
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
            .AddMigration<CreateNotes>()
            .AddMigration<AddBody>()
            .AddMigration<Broken>()
            .UseSqliteNative());

        var sp = services.BuildServiceProvider();
        await using var spScope = sp.ConfigureAwait(false);

        var ex = await Assert.ThrowsAsync<EdgeMigrationException>(async () =>
            await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false));

        Assert.Equal(3, ex.Version);
        Assert.Equal("broken", ex.Name);

        // The database is left at the last successful user_version.
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "m.db")}");
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await connection.ScalarAsync<int>("PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class TestPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
