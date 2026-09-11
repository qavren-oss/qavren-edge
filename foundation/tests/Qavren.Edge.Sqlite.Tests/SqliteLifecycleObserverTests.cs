using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class SqliteLifecycleObserverTests
{
    private static async Task WriteWithoutCheckpointAsync(EdgeTestHost host, CancellationToken ct)
    {
        var connection = await host.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS wal_probe(id INTEGER PRIMARY KEY, blob BLOB)",
                cancellationToken: ct).ConfigureAwait(false);
            for (var i = 0; i < 200; i++)
            {
                SqliteParameter[] parameters = [new SqliteParameter("$b", new byte[4096])];
                await connection.ExecuteAsync(
                    "INSERT INTO wal_probe(blob) VALUES ($b)",
                    parameters,
                    ct).ConfigureAwait(false);
            }
        }
    }

    [Fact]
    public async Task Sleeping_CheckpointsAndTruncatesTheWal()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);

        await WriteWithoutCheckpointAsync(host, ct);

        var wal = host.Database.Path + "-wal";
        Assert.True(File.Exists(wal), "WAL mode should have produced a -wal file");
        Assert.True(new FileInfo(wal).Length > 0, "the WAL should hold un-checkpointed frames");

        await host.Lifecycle.RaiseSleepingAsync(ct);

        // wal_checkpoint(TRUNCATE) leaves the file present but zero-length.
        Assert.Equal(0, new FileInfo(wal).Length);
    }

    [Fact]
    public async Task Sleeping_RunsWithoutObserverFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);

        await host.Lifecycle.RaiseSleepingAsync(ct);

        var record = host.Lifecycle.RecentEvents[0];
        Assert.Equal(EdgeLifecycleEventKind.Sleeping, record.Kind);
        Assert.Equal(0, record.ObserverFailures);
    }

    [Theory]
    [InlineData(EdgeMemoryPressure.Low)]
    [InlineData(EdgeMemoryPressure.Moderate)]
    public async Task MemoryPressure_BelowCritical_LeavesThePoolAlone(EdgeMemoryPressure level)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "delete-while-open is only observable on Windows");

        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);

        var connection = await host.Database.OpenConnectionAsync(ct);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync("CREATE TABLE keepalive(x INTEGER)", cancellationToken: ct);
        }

        await host.Lifecycle.RaiseMemoryPressureAsync(level, ct);

        // The physical connection is still pooled, so the file is still open and cannot be deleted.
        Assert.Throws<IOException>(() => File.Delete(host.Database.Path));
    }

    [Fact]
    public async Task MemoryPressure_Critical_ReleasesPooledPhysicalConnections()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);

        var connection = await host.Database.OpenConnectionAsync(ct);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync("CREATE TABLE keepalive(x INTEGER)", cancellationToken: ct);
        }

        await host.Lifecycle.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, ct);

        // SQLite does not open the database file with FILE_SHARE_DELETE, so on Windows the delete
        // succeeds only once every pooled physical connection has actually been disposed. That is
        // the observable proof that ClearAllPools ran.
        File.Delete(host.Database.Path);
        Assert.False(File.Exists(host.Database.Path));
    }

    [Fact]
    public async Task Stopping_ReleasesPooledPhysicalConnections()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);

        var connection = await host.Database.OpenConnectionAsync(ct);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync("CREATE TABLE keepalive(x INTEGER)", cancellationToken: ct);
        }

        await host.Lifecycle.RaiseStoppingAsync(ct);

        File.Delete(host.Database.Path);
        Assert.False(File.Exists(host.Database.Path));
    }

    [Fact]
    public async Task Observer_IsRegisteredExactlyOnceByAddSqlite()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);

        var observers = host.Services.GetServices<IEdgeLifecycleObserver>()
            .Where(o => o.GetType().Name == "SqliteLifecycleObserver")
            .ToArray();

        Assert.Single(observers);
    }
}
