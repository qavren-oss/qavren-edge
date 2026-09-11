using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.DeviceTests;

public class NativeSmokeTests
{
    private static async Task<ServiceProvider> StartAsync(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new DevicePaths());
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o => o.DatabaseName = "device-tests.db")
            .UseSqliteNative());

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(cancellationToken);
        return provider;
    }

    [Fact]
    public async Task NativeLibraryLoadsAndReportsVersions()
    {
        await using var services = await StartAsync(TestContext.Current.CancellationToken);

        var info = await services.GetRequiredService<IEdgeDatabase>()
            .GetInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("3.53.4", info.SqliteVersion);
        Assert.Equal("v0.1.9", info.VecVersion);
    }

    [Fact]
    public async Task Vec0AndFts5AreAvailableOnDevice()
    {
        await using var services = await StartAsync(TestContext.Current.CancellationToken);
        await using var connection = await services.GetRequiredService<IEdgeDatabase>()
            .OpenConnectionAsync(TestContext.Current.CancellationToken);

        await VecTable.CreateAsync(connection, "device_vec", dims: 4,
            cancellationToken: TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "CREATE VIRTUAL TABLE IF NOT EXISTS device_fts USING fts5(body)",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1L, await connection.ScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE name = 'device_vec'",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SandboxPathsAreUsable()
    {
        IEdgePaths paths = new DevicePaths();

        Assert.False(string.IsNullOrWhiteSpace(paths.Data));
        Assert.False(string.IsNullOrWhiteSpace(paths.Cache));

        var probe = Path.Combine(paths.Data, "qedge-probe.txt");
        File.WriteAllText(probe, "ok");
        Assert.Equal("ok", File.ReadAllText(probe));
        File.Delete(probe);
    }
}
