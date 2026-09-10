using Microsoft.Extensions.DependencyInjection;

namespace Qavren.Edge.Hosting;

/// <summary>
/// The single chaining surface every Qavren.Edge package extends. It is a thin wrapper over
/// <see cref="IServiceCollection"/> and holds no state of its own.
/// </summary>
public sealed class EdgeBuilder
{
    public EdgeBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    public IServiceCollection Services { get; }
}
