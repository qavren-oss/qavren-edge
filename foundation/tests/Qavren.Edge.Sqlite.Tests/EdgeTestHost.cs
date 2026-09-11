using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite.Native;

namespace Qavren.Edge.Sqlite.Tests;

/// <summary>Builds a fully wired provider over a scratch directory, and deletes it on dispose.</summary>
public sealed class EdgeTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private EdgeTestHost(ServiceProvider services, string root)
    {
        _services = services;
        Root = root;
    }

    public string Root { get; }

    public IServiceProvider Services => _services;

    public IEdgeDatabase Database => _services.GetRequiredService<IEdgeDatabase>();

    public IEdgeLifecycle Lifecycle => _services.GetRequiredService<IEdgeLifecycle>();

    public static async Task<EdgeTestHost> StartAsync(
        Action<EdgeBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging();

        // AddQavrenEdge registers IEdgePaths with TryAddSingleton, so this has to come first:
        // registering it inside the callback would come second and lose.
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));

        services.AddQavrenEdge(edge =>
        {
            if (configure is null)
            {
                edge.AddSqlite(o =>
                {
                    o.DatabaseName = "test.db";
                    o.Directory = root;
                });
                edge.UseSqliteNative();
            }
            else
            {
                configure(edge);
            }
        });

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return new EdgeTestHost(provider, root);
    }

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await _services.DisposeAsync().ConfigureAwait(false);
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
