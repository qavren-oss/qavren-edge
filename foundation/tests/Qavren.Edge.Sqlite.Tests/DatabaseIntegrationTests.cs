using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

// CA2007 is an error repo-wide (.editorconfig) and the analyzer's test-method exemption does not
// cover the implicit DisposeAsync of an `await using`, so every disposal here is written as
// `await using (x.ConfigureAwait(false))` - the same shape EdgeDatabase.cs uses.
public class DatabaseIntegrationTests
{
    [Fact]
    public async Task NativeProvider_ReportsTheExpectedVersions()
    {
        var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var hostScope = host.ConfigureAwait(false);

        var info = await host.Database.GetInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("3.53.4", info.SqliteVersion);
        Assert.Equal("v0.1.9", info.VecVersion);   // leading 'v' is part of SQLITE_VEC_VERSION
        Assert.False(info.IsEncrypted);
    }

    [Fact]
    public async Task PerOpenPragmas_AreApplied()
    {
        var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var hostScope = host.ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var connectionScope = connection.ConfigureAwait(false);

        Assert.Equal("wal", (await connection.ScalarAsync<string>("PRAGMA journal_mode", cancellationToken: TestContext.Current.CancellationToken))!.ToLowerInvariant());
        Assert.Equal(1, await connection.ScalarAsync<int>("PRAGMA synchronous", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await connection.ScalarAsync<int>("PRAGMA foreign_keys", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(-8192, await connection.ScalarAsync<int>("PRAGMA cache_size", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PooledReuse_KeepsThePragmasAndTheProvider()
    {
        var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var hostScope = host.ConfigureAwait(false);

        for (var i = 0; i < 5; i++)
        {
            var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using (connection.ConfigureAwait(false))
            {
                Assert.Equal("v0.1.9", await connection.ScalarAsync<string>("SELECT vec_version()", cancellationToken: TestContext.Current.CancellationToken));
            }
        }

        // Microsoft.Data.Sqlite's SqliteConnection static ctor has now run many times; the provider
        // must still be ours, proving FreezeProvider held.
        Assert.Equal("qedge_sqlite3", SQLitePCL.raw.GetNativeLibraryName());
    }

    [Fact]
    public async Task VecTableAndKnnRoundTrip()
    {
        var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var hostScope = host.ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var connectionScope = connection.ConfigureAwait(false);

        await VecTable.CreateAsync(connection, "v", dims: 4, cancellationToken: TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(
            "INSERT INTO v(rowid, embedding) VALUES ($id, $e)",
            [new SqliteParameter("$id", 1L), new SqliteParameter("$e", VecBlob.From([1f, 0f, 0f, 0f]))],
            TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO v(rowid, embedding) VALUES ($id, $e)",
            [new SqliteParameter("$id", 2L), new SqliteParameter("$e", VecBlob.From([0f, 1f, 0f, 0f]))],
            TestContext.Current.CancellationToken);

        float[] query = [1f, 0f, 0f, 0f];
        var hits = await Knn.QueryAsync(
            connection, "v", query.AsMemory(), k: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        var hit = Assert.Single(hits);
        Assert.Equal(1L, hit.RowId);
        Assert.True(hit.Distance < 1e-5f, $"expected ~0 cosine distance, got {hit.Distance}");
    }

    [Fact]
    public async Task Fts5_CreateAndMatch()
    {
        var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var hostScope = host.ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var connectionScope = connection.ConfigureAwait(false);

        await FtsTable.CreateAsync(connection, "f", ["title", "body"], cancellationToken: TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO f(title, body) VALUES ('hello', 'world of sqlite')",
            cancellationToken: TestContext.Current.CancellationToken);

        var count = await connection.ScalarAsync<long>(
            "SELECT count(*) FROM f WHERE f MATCH 'sqlite'",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Checkpoint_TruncatesTheWal()
    {
        var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var hostScope = host.ConfigureAwait(false);

        var writer = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (writer.ConfigureAwait(false))
        {
            await writer.ExecuteAsync("CREATE TABLE t(x)", cancellationToken: TestContext.Current.CancellationToken);
            for (var i = 0; i < 200; i++)
            {
                await writer.ExecuteAsync("INSERT INTO t VALUES (randomblob(512))", cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        await host.Database.CheckpointAsync(TestContext.Current.CancellationToken);

        var wal = host.Database.Path + "-wal";
        Assert.True(!File.Exists(wal) || new FileInfo(wal).Length == 0, "the WAL should be truncated after a checkpoint");
    }
}
