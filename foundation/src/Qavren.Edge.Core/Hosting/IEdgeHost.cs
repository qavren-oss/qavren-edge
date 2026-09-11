namespace Qavren.Edge.Hosting;

/// <summary>
/// Owns one-time startup. Every Qavren.Edge service entry point calls
/// <see cref="EnsureStartedAsync"/> first, so a startup failure surfaces on first use
/// with its real cause and no caller ever races migrations.
/// </summary>
public interface IEdgeHost
{
    /// <summary>Begins startup exactly once. Safe to call repeatedly and from any thread. Never blocks.</summary>
    void Start();

    /// <summary>Completes when every startup task has finished; faults with the first failure.</summary>
    Task Started { get; }

    /// <summary>Calls <see cref="Start"/> if needed, awaits <see cref="Started"/>, and rethrows its fault.</summary>
    ValueTask EnsureStartedAsync(CancellationToken cancellationToken = default);

    /// <summary>Raises <c>Stopping</c> on the lifecycle hub and disposes owned resources.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
