using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// Spec 14.2's L0 observer. Registered with <c>TryAddEnumerable</c>, exactly as SP1's SQLite
/// observer is, and derived from SP1's no-op <see cref="EdgeLifecycleObserver"/>.
/// </summary>
/// <remarks>
/// In this wave the observer is the pressure latch and nothing else. It does NOT touch a batch
/// size: that lives in <c>OnnxEmbeddingOptions</c> in L1, which this L0 observer cannot see, and
/// keeping the signal in L0 with the reaction in L1 is what keeps spec 4.1's dependency direction
/// true. It does not drop a session either - dropping a 23 MB session that is about to be needed
/// again would be a worse trade than shrinking a batch. The <c>DropAsync</c> wiring for
/// <c>Critical</c> and <c>Stopping</c> arrives with the session host.
/// </remarks>
internal sealed class OnnxLifecycleObserver(IEdgeResourceMonitor monitor) : EdgeLifecycleObserver
{
    /// <inheritdoc />
    public override Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
    {
        monitor.SetPressure(level);
        return Task.CompletedTask;
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
    /// Drops every session once the session host exists. <c>OrtEnv</c> is NOT disposed: it is a
    /// process-wide singleton with a one-shot options hook, and tearing it down would silently
    /// break a second Edge host in the same process.
    /// </remarks>
    public override Task OnStoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Sleeping is deliberately not overridden: the CoreML cache is already on disk.
}
