using Microsoft.Extensions.DependencyInjection;

namespace Qavren.Edge.Hosting;

/// <summary>
/// The single chaining surface every Qavren.Edge package extends. It is a thin wrapper over
/// <see cref="IServiceCollection"/> and holds no state of its own.
/// </summary>
public sealed class EdgeBuilder
{
    /// <summary>Wraps the given <paramref name="services"/> collection.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public EdgeBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <summary>The underlying service collection every package extension registers into.</summary>
    public IServiceCollection Services { get; }
}
