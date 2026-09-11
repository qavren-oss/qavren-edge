using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
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
