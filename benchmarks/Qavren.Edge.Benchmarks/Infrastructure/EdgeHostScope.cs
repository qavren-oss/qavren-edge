using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;

namespace Qavren.Edge.Benchmarks.Infrastructure;

/// <summary>
/// One started Qavren.Edge container over a scratch directory, composed the way a consumer composes
/// it: <c>AddQavrenEdge</c>, then the builder calls the benchmark asks for. Disposing it disposes the
/// container and deletes the directory.
/// </summary>
internal sealed class EdgeHostScope : IDisposable
{
    private readonly ServiceProvider _services;

    private EdgeHostScope(ServiceProvider services, ScratchPaths paths)
    {
        _services = services;
        Paths = paths;
    }

    public IServiceProvider Services => _services;

    public ScratchPaths Paths { get; }

    /// <summary>The unkeyed database, when the composition registered one.</summary>
    public IEdgeDatabase Database => _services.GetRequiredService<IEdgeDatabase>();

    /// <summary>
    /// Builds and starts a container. <paramref name="sqlite"/> adds the native provider and one
    /// file database under the scratch directory before <paramref name="configure"/> runs.
    /// </summary>
    public static EdgeHostScope Start(string area, bool sqlite, Action<EdgeBuilder, ScratchPaths>? configure = null)
    {
        var paths = ScratchPaths.Create(area);
        var services = new ServiceCollection();
        services.AddLogging();

        // AddQavrenEdge registers IEdgePaths with TryAddSingleton, so this has to come first.
        services.AddSingleton<IEdgePaths>(paths);

        services.AddQavrenEdge(edge =>
        {
            if (sqlite)
            {
                edge.UseSqliteNative();
                edge.AddSqlite(o =>
                {
                    o.DatabaseName = "bench.db";
                    o.Directory = paths.Root;
                });
            }

            configure?.Invoke(edge, paths);
        });

        var provider = services.BuildServiceProvider();
        try
        {
            provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            paths.Delete();
            throw;
        }

        return new EdgeHostScope(provider, paths);
    }

    public void Dispose()
    {
        // Async: some Qavren.Edge singletons are IAsyncDisposable only, and a sync Dispose throws.
        _services.DisposeAsync().AsTask().GetAwaiter().GetResult();

        // Microsoft.Data.Sqlite pools connections, and a pooled connection keeps the database and its
        // WAL open - which is what left 1 GB of scratch databases behind before this line existed.
        SqliteConnection.ClearAllPools();
        Paths.Delete();
    }
}
