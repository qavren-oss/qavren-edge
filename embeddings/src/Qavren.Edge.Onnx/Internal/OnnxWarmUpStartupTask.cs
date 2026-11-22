using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// Startup order 220. Creates the session so the first real call pays neither graph optimisation nor
/// a CoreML compile, and <b>runs no inference</b>: a batch needs a tokenizer and a generator, and
/// both live in <c>Qavren.Edge.Embeddings.Onnx</c>. Its load-plus-batch sibling there is
/// <c>WarmUpEmbeddingsAtStartup</c>, registered at this same order.
/// </summary>
/// <remarks>
/// <see cref="IServiceProvider"/> rather than the session host itself for the same reason as the
/// provisioning task: <c>EdgeHost</c>'s constructor eagerly resolves every startup task, and the
/// session host depends on <c>IEdgeHost</c>. It acquires through the ordinary <c>AcquireAsync</c>:
/// <c>IEdgeHost.EnsureStartedAsync</c> returns at once when a startup task calls it, so the acquire
/// does not wait on this task's own completion.
/// </remarks>
internal sealed class OnnxWarmUpStartupTask(IServiceProvider services, string modelId) : IEdgeStartupTask
{
    /// <inheritdoc />
    public int Order => EdgeAiStartupOrder.SessionWarmUp;

    /// <summary>The model this task loads.</summary>
    public string ModelId => modelId;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var host = services.GetRequiredService<OnnxSessionHost>();

        // Taken and returned immediately: warm-up wants the session created, not held.
        using var lease = await host.AcquireAsync(modelId, cancellationToken).ConfigureAwait(false);
    }
}
