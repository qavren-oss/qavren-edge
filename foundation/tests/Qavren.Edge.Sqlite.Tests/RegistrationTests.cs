using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite.Native;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class RegistrationTests
{
    [Fact]
    public void AddSqlite_TwiceWithTheSameName_ThrowsAtBuildTime()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var ex = Assert.Throws<EdgeConfigurationException>(() =>
            services.AddQavrenEdge(edge => edge
                .AddSqlite(o => o.DatabaseName = "a.db")
                .AddSqlite(o => o.DatabaseName = "b.db")));

        Assert.Equal(EdgeErrorCode.DuplicateDatabaseName, ex.Code);
    }

    [Fact]
    public void AddSqlite_WithDistinctNames_RegistersKeyedDatabases()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o => o.DatabaseName = "default.db")
            .AddSqlite("corpus", o => o.DatabaseName = "corpus.db"));

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IEdgeDatabase>());
        Assert.NotNull(sp.GetKeyedService<IEdgeDatabase>("corpus"));
    }

    [Fact]
    public async Task NoNativeProviderRegistered_FaultsStartupWithAConfigurationError()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddSqlite(o => o.DatabaseName = ":memory:"));
        using var sp = services.BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<EdgeConfigurationException>(async () =>
            await sp.GetRequiredService<IEdgeHost>()
                .EnsureStartedAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false));

        Assert.Equal(EdgeErrorCode.NoNativeProviderRegistered, ex.Code);
    }

    [Fact]
    public void Migrations_MustHaveUniqueAscendingVersions()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var ex = Assert.Throws<EdgeConfigurationException>(() =>
            services.AddQavrenEdge(edge => edge
                .AddSqlite(o => o.DatabaseName = ":memory:")
                .AddMigration<M1>()
                .AddMigration<M1Duplicate>()));

        Assert.Equal(EdgeErrorCode.MigrationVersionConflict, ex.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheUnkeyedDatabaseIsTheUnnamedOneWhicheverOrderTheTwoWereRegisteredIn(bool defaultFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge =>
        {
            if (defaultFirst)
            {
                edge.AddSqlite(o => o.DatabaseName = "default.db")
                    .AddSqlite("corpus", o => o.DatabaseName = "corpus.db");
            }
            else
            {
                edge.AddSqlite("corpus", o => o.DatabaseName = "corpus.db")
                    .AddSqlite(o => o.DatabaseName = "default.db");
            }
        });

        using var sp = services.BuildServiceProvider();

        // MSDI hands back the LAST unkeyed registration. Left to registration order, "default then
        // corpus" made this the corpus database - while the vector store's and ingestion's
        // migrations, which name the unnamed database explicitly, went to the other file.
        Assert.Equal("(default)", sp.GetRequiredService<IEdgeDatabase>().Name);
        Assert.Equal("corpus", sp.GetRequiredKeyedService<IEdgeDatabase>("corpus").Name);

        // Reordered, not removed: the lifecycle observers enumerate every database.
        Assert.Equal(
            ["(default)", "corpus"],
            sp.GetServices<IEdgeDatabase>().Select(d => d.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WithNoUnnamedDatabaseTheUnkeyedOneIsStillTheLastRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddSqlite("a", o => o.DatabaseName = "a.db")
            .AddSqlite("b", o => o.DatabaseName = "b.db"));

        using var sp = services.BuildServiceProvider();

        Assert.Equal("b", sp.GetRequiredService<IEdgeDatabase>().Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AMigrationVersionBelowOneIsRefusedBecauseTheMigratorWouldNeverRunIt(int version)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // A new database is at user_version 0 and the migrator runs the versions ABOVE the file's,
        // so a first migration numbered 0 was skipped without a word.
        var ex = Assert.Throws<EdgeConfigurationException>(() =>
            services.AddQavrenEdge(edge => edge
                .AddSqlite(o => o.DatabaseName = ":memory:")
                .AddMigrations([new Numbered(version)])));

        Assert.Equal(EdgeErrorCode.MigrationVersionConflict, ex.Code);
    }

    [Fact]
    public async Task AnOpenThatFailsAfterTheFileIsOpenGivesItsHandleBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "leak.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o =>
            {
                o.DatabaseName = "leak.db";
                o.Directory = root;

                // "PRAGMA cache_size = --1;": the second '-' opens a SQL comment, so the per-open
                // pragma is a syntax error raised AFTER the connection is open. It stands in for
                // the case that cannot be staged, a cancellation between the open and its pragmas.
                o.CacheSizeKiB = -1;
            })
            .UseSqliteNative());

        var sp = services.BuildServiceProvider();
        await using (sp.ConfigureAwait(true))
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(ct).ConfigureAwait(false))
                .ConfigureAwait(true);

            // Clearing the pools closes every connection that was RETURNED. One that was opened and
            // then dropped on the floor is still checked out, still holds the file, and on Windows
            // makes this delete a sharing violation.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);

            Assert.False(File.Exists(file));
        }
    }

    private sealed class Numbered(int version) : IEdgeMigration
    {
        public int Version => version;

        public string Name => "numbered";

        public Task UpAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class M1 : IEdgeMigration
    {
        public int Version => 1;

        public string Name => "one";

        public Task UpAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class M1Duplicate : IEdgeMigration
    {
        public int Version => 1;

        public string Name => "also one";

        public Task UpAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
