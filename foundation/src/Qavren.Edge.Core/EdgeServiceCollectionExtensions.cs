using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge;

/// <summary>The one entry point every consumer calls to wire Qavren.Edge into a container.</summary>
public static class EdgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Qavren.Edge core. Idempotent: calling it twice adds one host, one hub,
    /// one diagnostics service, and applies both configuration callbacks.
    /// </summary>
    public static IServiceCollection AddQavrenEdge(this IServiceCollection services, Action<EdgeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEdgePaths>(sp =>
            new DefaultEdgePaths(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.AppName));
        services.TryAddSingleton<IEdgeLifecycle, EdgeLifecycleHub>();
        services.TryAddSingleton<EdgeHost>();
        services.TryAddSingleton<IEdgeHost>(sp => sp.GetRequiredService<EdgeHost>());
        services.TryAddSingleton<IEdgeDiagnostics, EdgeDiagnostics>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EdgeHostedService>());

        configure(new EdgeBuilder(services));
        return services;
    }

    /// <summary>Configures <see cref="EdgeOptions"/> without adding another host.</summary>
    public static EdgeBuilder Configure(this EdgeBuilder builder, Action<EdgeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure(configure);
        return builder;
    }
}
