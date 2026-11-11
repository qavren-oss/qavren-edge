using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.Sqlite.Native.Cipher;
using Xunit;

namespace Qavren.Edge.Sqlite.Cipher.Tests;

/// <summary>
/// Spec 8.8 / 13: open, rekey, wrong key, raw-key and passphrase paths, pooled reuse keeps the
/// key, and a plain provider with a key configured is a configuration error.
/// </summary>
/// <remarks>
/// The cipher provider needs its own test process: <c>raw.FreezeProvider()</c> and the DllImport
/// resolver are both process-wide, so the first provider that installs owns the process.
/// </remarks>
public class CipherTests
{
    private static readonly byte[] KeyA = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    private static readonly byte[] KeyB = [.. Enumerable.Range(100, 32).Select(i => (byte)i)];

    private static bool NativePresent =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "qedge_sqlcipher.dll")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libqedge_sqlcipher.so")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libqedge_sqlcipher.dylib"));

    [Fact]
    public async Task RawKey_OpensWritesAndReopens()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine; run build-windows.ps1 -Cipher.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        var first = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct).ConfigureAwait(true);
        await using (first.ConfigureAwait(true))
        {
            var db = first.GetRequiredService<IEdgeDatabase>();
            Assert.True(db.IsEncrypted);

            var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (connection.ConfigureAwait(true))
            {
                await connection.ExecuteAsync("CREATE TABLE t(x TEXT)", cancellationToken: ct).ConfigureAwait(true);
                await connection.ExecuteAsync("INSERT INTO t VALUES('hello')", cancellationToken: ct).ConfigureAwait(true);
            }
        }

        SqliteConnection.ClearAllPools();

        // Same process, same frozen provider: reopen and read back.
        var second = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct).ConfigureAwait(true);
        await using (second.ConfigureAwait(true))
        {
            var connection = await second.GetRequiredService<IEdgeDatabase>()
                .OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (connection.ConfigureAwait(true))
            {
                Assert.Equal(
                    "hello",
                    await connection.ScalarAsync<string>("SELECT x FROM t", cancellationToken: ct).ConfigureAwait(true));
            }
        }
    }

    [Fact]
    public async Task PooledReuse_KeepsTheKey()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct).ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var db = sp.GetRequiredService<IEdgeDatabase>();

            var created = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (created.ConfigureAwait(true))
            {
                await created.ExecuteAsync("CREATE TABLE t(x TEXT)", cancellationToken: ct).ConfigureAwait(true);
            }

            // Each iteration returns its connection to the pool and takes it back out again.
            for (var i = 0; i < 10; i++)
            {
                var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
                await using (connection.ConfigureAwait(true))
                {
                    Assert.Equal(
                        0L,
                        await connection.ScalarAsync<long>("SELECT count(*) FROM t", cancellationToken: ct)
                            .ConfigureAwait(true));
                }
            }
        }
    }

    [Fact]
    public async Task WrongKey_RaisesEdgeDatabaseKeyExceptionAtStartup()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct).ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var connection = await sp.GetRequiredService<IEdgeDatabase>().OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (connection.ConfigureAwait(true))
            {
                await connection.ExecuteAsync("CREATE TABLE t(x)", cancellationToken: ct).ConfigureAwait(true);
            }
        }

        SqliteConnection.ClearAllPools();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            // StartAsync awaits startup, and startup task 10 reads sqlite_master, so the wrong key
            // is rejected before this ever gets a service provider back.
            var wrong = await StartAsync(root, SqliteKey.FromRawBytes(KeyB), ct).ConfigureAwait(true);
            await wrong.DisposeAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [Fact]
    public async Task Passphrase_AlsoWorks()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        var sp = await StartAsync(root, SqliteKey.FromPassphrase("correct horse battery staple"), ct)
            .ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var connection = await sp.GetRequiredService<IEdgeDatabase>().OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (connection.ConfigureAwait(true))
            {
                await connection.ExecuteAsync("CREATE TABLE t(x)", cancellationToken: ct).ConfigureAwait(true);
                Assert.Equal(
                    "v0.1.9",
                    await connection.ScalarAsync<string>("SELECT vec_version()", cancellationToken: ct)
                        .ConfigureAwait(true));
            }
        }
    }

    [Fact]
    public async Task Rekey_ChangesTheKey()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct).ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var db = sp.GetRequiredService<IEdgeDatabase>();

            var seeded = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (seeded.ConfigureAwait(true))
            {
                await seeded.ExecuteAsync("CREATE TABLE t(x TEXT)", cancellationToken: ct).ConfigureAwait(true);
                await seeded.ExecuteAsync("INSERT INTO t VALUES('kept')", cancellationToken: ct).ConfigureAwait(true);
            }

            await db.RekeyAsync(SqliteKey.FromRawBytes(KeyB), ct).ConfigureAwait(true);

            var reopened = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (reopened.ConfigureAwait(true))
            {
                Assert.Equal(
                    "kept",
                    await reopened.ScalarAsync<string>("SELECT x FROM t", cancellationToken: ct).ConfigureAwait(true));
            }
        }
    }

    [Fact]
    public async Task Rekey_NeverLeavesTheProcessOnAKeyTheFileIsNoLongerOn()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;
        bool cancelled;

        var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct).ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var db = sp.GetRequiredService<IEdgeDatabase>();

            // Enough pages that re-encrypting them takes far longer than the cancellation below, so
            // it lands DURING the PRAGMA - which runs to completion whatever its token does.
            var seeded = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (seeded.ConfigureAwait(true))
            {
                await seeded.ExecuteAsync("CREATE TABLE t(x BLOB)", cancellationToken: ct).ConfigureAwait(true);
                await seeded.ExecuteAsync(
                    "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 20000) " +
                    "INSERT INTO t SELECT randomblob(2000) FROM n",
                    cancellationToken: ct).ConfigureAwait(true);
            }

            using var impatient = CancellationTokenSource.CreateLinkedTokenSource(ct);
            impatient.CancelAfter(TimeSpan.FromMilliseconds(25));

            try
            {
                await db.RekeyAsync(SqliteKey.FromRawBytes(KeyB), impatient.Token).ConfigureAwait(true);
                cancelled = false;
            }
            catch (OperationCanceledException)
            {
                // Only legitimate BEFORE the point of no return. If the file had been rekeyed and
                // this were thrown anyway, the caller would keep key A, the process would too, and
                // the open below would fail - forever, on every later launch.
                cancelled = true;
            }

            var reopened = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (reopened.ConfigureAwait(true))
            {
                Assert.Equal(
                    20000L,
                    await reopened.ScalarAsync<long>("SELECT count(*) FROM t", cancellationToken: ct).ConfigureAwait(true));
            }
        }

        SqliteConnection.ClearAllPools();

        // And what the caller was told matches the file: "cancelled" means still key A.
        var after = await StartAsync(root, SqliteKey.FromRawBytes(cancelled ? KeyA : KeyB), ct).ConfigureAwait(true);
        await using (after.ConfigureAwait(true))
        {
            var connection = await after.GetRequiredService<IEdgeDatabase>().OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (connection.ConfigureAwait(true))
            {
                Assert.Equal(
                    20000L,
                    await connection.ScalarAsync<long>("SELECT count(*) FROM t", cancellationToken: ct).ConfigureAwait(true));
            }
        }
    }

    [Fact]
    public async Task TheDiagnosticsReportDescribesTheCipherNative()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var ct = TestContext.Current.CancellationToken;

        var sp = await StartAsync(NewRoot(), SqliteKey.FromRawBytes(KeyA), ct).ConfigureAwait(true);
        await using (sp.ConfigureAwait(true))
        {
            var report = sp.GetRequiredService<Qavren.Edge.Diagnostics.IEdgeDiagnostics>().Report();

            // UseSqliteNativeCipher() registered no contributor, so an encrypted app's report had
            // an empty native block: no library, no path, no versions.
            Assert.Contains(report.Components, c => c.Name == "Qavren.Edge.Sqlite.Native.Cipher");
            Assert.Equal("v0.1.9", report.Native["vecVersion"]);
            Assert.False(string.IsNullOrEmpty(report.Native["sqliteVersion"]));
            Assert.False(string.IsNullOrEmpty(report.Native["cipherVersion"]));

            // resolvedPath is not asserted: the DllImport resolver is installed once per process, by
            // whichever provider instance got there first, and this test process creates several.
            Assert.True(report.Native.ContainsKey("resolvedPath"));
        }
    }

    [Fact]
    public void PlainProviderPlusKey_IsAConfigurationError()
    {
        // The plan's note says this test never starts the host, but OpenConnectionAsync awaits
        // EnsureStartedAsync, so the plain provider's Install() does run. Install() pins the
        // process's DllImport resolver to whichever provider installs FIRST, so letting the plain
        // one win here would load qedge_sqlite3 and break every cipher test that ran afterwards.
        // Installing the cipher provider first is idempotent, keeps the process on the cipher
        // native, and leaves the assertion below untouched: the error is raised from the
        // DI-registered provider's SupportsEncryption, not from the loaded library.
        if (NativePresent)
        {
            new QedgeSqlCipherNativeProvider().Install();
        }

        var root = NewRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o => { o.DatabaseName = "x.db"; o.Directory = root; o.Key = SqliteKey.FromRawBytes(KeyA); })
            .UseSqliteNative());

        using var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<IEdgeDatabase>();

        var ex = Assert.Throws<EdgeConfigurationException>(() =>
            db.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult());

        Assert.Equal(EdgeErrorCode.EncryptionKeyWithoutCipherProvider, ex.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInMemoryDatabaseIsOneDatabaseUnderTheCipherBuildWithOrWithoutAKey(bool keyed)
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(NewRoot()));
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o =>
            {
                o.DatabaseName = ":memory:";
                o.Key = keyed ? SqliteKey.FromRawBytes(KeyA) : null;
            })
            .UseSqliteNativeCipher());

        var sp = services.BuildServiceProvider();
        await using (sp.ConfigureAwait(true))
        {
            await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(ct).ConfigureAwait(true);
            var db = sp.GetRequiredService<IEdgeDatabase>();

            var writer = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (writer.ConfigureAwait(true))
            {
                await writer.ExecuteAsync("CREATE TABLE t(x TEXT)", cancellationToken: ct).ConfigureAwait(true);
                await writer.ExecuteAsync("INSERT INTO t VALUES('hello')", cancellationToken: ct).ConfigureAwait(true);
            }

            // A second connection, opened after the first has closed. With a key it also has to
            // read pages the first one encrypted, so each connection keying itself is not enough:
            // they have to be keying the same database.
            var reader = await db.OpenConnectionAsync(ct).ConfigureAwait(true);
            await using (reader.ConfigureAwait(true))
            {
                Assert.Equal(
                    "hello",
                    await reader.ScalarAsync<string>("SELECT x FROM t", cancellationToken: ct).ConfigureAwait(true));
            }
        }
    }

    private static async Task<ServiceProvider> StartAsync(string root, SqliteKey? key, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o =>
            {
                o.DatabaseName = "secret.db";
                o.Directory = root;
                o.Key = key;
            })
            .UseSqliteNativeCipher());

        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(ct).ConfigureAwait(false);
        return sp;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-cipher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FixedPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
