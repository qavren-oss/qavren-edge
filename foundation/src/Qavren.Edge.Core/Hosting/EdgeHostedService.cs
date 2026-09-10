using Microsoft.Extensions.Hosting;

namespace Qavren.Edge.Hosting;

/// <summary>
/// Generic-host integration: starts the Edge host and awaits it, so a startup failure fails
/// <c>IHost.StartAsync</c>. MAUI does not use this — the lifecycle bridge calls
/// <see cref="IEdgeHost.Start"/> from the platform launch event and never blocks it.
/// </summary>
public sealed class EdgeHostedService(IEdgeHost host) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
        => await host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
}
