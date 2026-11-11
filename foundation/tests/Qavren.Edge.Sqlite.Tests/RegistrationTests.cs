using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
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

        // Resolved on its own line, outside the assertion. With both inside it this test passed on
        // a host that could not be resolved at all: the exception came out of GetRequiredService,
        // startup never ran, and nothing that takes an IEdgeHost could be built.
        var host = sp.GetRequiredService<IEdgeHost>();

        var ex = await Assert.ThrowsAsync<EdgeConfigurationException>(async () =>
            await host.EnsureStartedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));

        Assert.Equal(EdgeErrorCode.NoNativeProviderRegistered, ex.Code);
        Assert.True(host.Started.IsFaulted);
    }

    [Fact]
    public async Task MoreThanOneNativeProvider_FaultsStartupWithAConfigurationError()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddSqlite(o => o.DatabaseName = ":memory:"));
        services.AddSingleton<ISqliteNativeProvider>(new NamedProvider("one"));
        services.AddSingleton<ISqliteNativeProvider>(new NamedProvider("two"));
        using var sp = services.BuildServiceProvider();

        var host = sp.GetRequiredService<IEdgeHost>();

        var ex = await Assert.ThrowsAsync<EdgeConfigurationException>(async () =>
            await host.EnsureStartedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));

        Assert.Equal(EdgeErrorCode.MultipleNativeProvidersRegistered, ex.Code);
        Assert.Contains("one, two", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoNativeProviderOpeningTheDatabaseRethrowsTheStartupFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddSqlite(o => o.DatabaseName = ":memory:"));
        using var sp = services.BuildServiceProvider();

        var database = sp.GetRequiredService<IEdgeDatabase>();

        var ex = await Assert.ThrowsAsync<EdgeConfigurationException>(async () =>
        {
            var connection = await database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        });

        // The one fault every caller is handed, not a second exception that happens to look like it.
        Assert.Equal(EdgeErrorCode.NoNativeProviderRegistered, ex.Code);
        Assert.Same(ex, sp.GetRequiredService<IEdgeHost>().Started.Exception?.InnerException);
    }

    [Fact]
    public async Task WithNoNativeProviderTheDiagnosticsReportIsStillProducedAndCarriesTheFault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddSqlite(o => o.DatabaseName = ":memory:"));
        using var sp = services.BuildServiceProvider();

        await Assert.ThrowsAsync<EdgeConfigurationException>(async () =>
            await sp.GetRequiredService<IEdgeHost>()
                .EnsureStartedAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false));

        // A report is what somebody asks for once startup has failed. This contributor used to take
        // the provider as a constructor argument, so with none registered the whole report was a
        // container error about a type the app never mentions.
        var report = sp.GetRequiredService<IEdgeDiagnostics>().Report();

        var failed = Assert.Single(report.Startup, task => task.Error is not null);
        Assert.Contains("No SQLite native provider is registered", failed.Error, StringComparison.Ordinal);

        var sqlite = Assert.Single(report.Components, component => component.Name == "Qavren.Edge.Sqlite");
        Assert.Equal(":memory:", sqlite.Details["db[(default)].path"]);
        Assert.Contains(
            nameof(EdgeErrorCode.NoNativeProviderRegistered),
            sqlite.Details["nativeProvider"],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartupTaskThatOpensTheDatabaseAheadOfTheOpenTaskGetsTheSameConfigurationError()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddSqlite(o => o.DatabaseName = ":memory:"));

        // A startup task is let straight through EnsureStartedAsync, so one ordered ahead of the
        // open task reaches the connection before anything has looked for a provider. It is owed
        // the documented error like everybody else, not whatever SQLitePCL says about having none.
        services.AddSingleton<IEdgeStartupTask>(sp => new OpensAheadOfTheOpenTask(sp));
        using var sp = services.BuildServiceProvider();

        var host = sp.GetRequiredService<EdgeHost>();

        var ex = await Assert.ThrowsAsync<EdgeConfigurationException>(async () =>
            await host.EnsureStartedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));

        Assert.Equal(EdgeErrorCode.NoNativeProviderRegistered, ex.Code);

        var failed = Assert.Single(host.StartupReports);
        Assert.Equal(typeof(OpensAheadOfTheOpenTask).FullName, failed.Name);
        Assert.NotNull(failed.Error);
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

    /// <summary>A provider that is only ever counted: nothing in these tests installs it.</summary>
    private sealed class NamedProvider(string name) : ISqliteNativeProvider
    {
        public string Name => name;

        public string LibraryName => name;

        public bool SupportsEncryption => false;

        public void Install()
        {
        }

        public SqliteNativeInfo Describe() => throw new InvalidOperationException("Install() has not run yet.");
    }

    private sealed class OpensAheadOfTheOpenTask(IServiceProvider services) : IEdgeStartupTask
    {
        public int Order => EdgeStartupOrder.DatabaseOpen - 1;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var connection = await services.GetRequiredService<IEdgeDatabase>()
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
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
