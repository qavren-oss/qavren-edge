using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// Spec 14.2's L0 observer. Registered with <c>TryAddEnumerable</c>, exactly as SP1's SQLite
/// observer is, and derived from SP1's no-op <see cref="EdgeLifecycleObserver"/>.
/// </summary>
/// <remarks>
/// The observer is the pressure latch AND the drop trigger. It does NOT touch a batch size: that
/// lives in <c>OnnxEmbeddingOptions</c> in L1, which this L0 observer cannot see, and keeping the
/// signal in L0 with the reaction in L1 is what keeps spec 4.1's dependency direction true.
/// <para>
/// It resolves the session host lazily out of <see cref="IServiceProvider"/> rather than taking it
/// in the constructor: <c>EdgeLifecycleHub</c> materialises every observer in its own constructor
/// and <c>EdgeHost</c> takes the hub, so a constructor dependency on a session host that itself
/// depends on <c>IEdgeHost</c> is a DI cycle.
/// </para>
/// </remarks>
internal sealed class OnnxLifecycleObserver(IEdgeResourceMonitor monitor, IServiceProvider services)
    : EdgeLifecycleObserver
{
    /// <summary>
    /// How long the drain may hold an OS memory warning. The hub awaits its observers, and an iOS
    /// memory warning is not a place to block.
    /// </summary>
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public override async Task OnMemoryPressureAsync(
        EdgeMemoryPressure level,
        CancellationToken cancellationToken)
    {
        monitor.SetPressure(level);

        if (level != EdgeMemoryPressure.Critical)
        {
            return;
        }

        if (TryGetHost() is not { } host)
        {
            return;
        }

        // Both, and in this order: a multi-second CoreML compile already in flight has to abort
        // cooperatively rather than run to completion into a kill.
        host.CancelLoadsInFlight();

        var drop = host.DropAsync(includePinned: false, cancellationToken);
        await Task.WhenAny(drop, Task.Delay(DrainBudget, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Clears the latch with <see langword="null"/>, not with a <c>None</c> member: SP1's merged
    /// <see cref="EdgeMemoryPressure"/> has none, and adding one would renumber the existing three.
    /// Does not pre-warm.
    /// </remarks>
    public override Task OnResumedAsync(CancellationToken cancellationToken)
    {
        monitor.SetPressure(null);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Drops every session, pinned ones included. <c>OrtEnv</c> is NOT disposed: it is a
    /// process-wide singleton with a one-shot options hook, and tearing it down would silently
    /// break a second Edge host in the same process.
    /// </remarks>
    public override async Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        if (TryGetHost() is not { } host)
        {
            return;
        }

        host.CancelLoadsInFlight();
        await host.DropAsync(includePinned: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Null when no session host is registered, or when the container is already being torn down -
    /// a shutdown race is not a reason to throw out of a lifecycle observer.
    /// </summary>
    private OnnxSessionHost? TryGetHost()
    {
        try
        {
            return services.GetService<OnnxSessionHost>();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    // Sleeping is deliberately not overridden: the CoreML cache is already on disk.
}
