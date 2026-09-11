using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion.Onnx.Internal;

/// <summary>
/// Order 390: builds the tokenizer ASYNCHRONOUSLY so the core's order-400 task, which resolves
/// <see cref="IChunkTokenizer"/> synchronously, finds it cached and blocks on nothing. Without this
/// task the first resolve would block the startup thread on SP2's provisioning - a vocabulary
/// download on a first launch - which on a phone is the main thread.
/// </summary>
/// <remarks>
/// Takes the <see cref="IServiceProvider"/> rather than the generator for the same reason every
/// SP2 startup task does: <c>EdgeHost</c>'s constructor eagerly resolves every startup task, and
/// resolving the generator here would drag <c>IEdgeHost</c> into its own construction path.
/// Silent when nothing is bound - no <c>AddIngestion</c> registration names this generator, or no
/// generator is registered at all - because the core's order-400 task reports both conditions
/// itself (6001 / 6208) and a second report would name the wrong call.
/// </remarks>
internal sealed class OnnxIngestionStartupTask(IServiceProvider services, OnnxIngestionBinding binding)
    : IEdgeStartupTask
{
    /// <summary>Ten before the core's validate task, and after every SP2 order (200-300).</summary>
    public const int PreValidate = EdgeIngestionStartupOrder.Validate - 10;

    /// <inheritdoc />
    public int Order => PreValidate;

    /// <summary>The binding this task warms.</summary>
    public OnnxIngestionBinding Binding => binding;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (binding.Tokenizer is not null)
        {
            return;
        }

        var registry = services.GetService<IngestionRegistry>();
        if (registry is null || !registry.All.Any(r =>
                string.Equals(r.Options.StoreName, binding.EmbeddingsName, StringComparison.Ordinal)))
        {
            return;
        }

        if (binding.TryResolvePreset(services) is null)
        {
            return;
        }

        await binding.ResolveAsync(services, cancellationToken).ConfigureAwait(false);
    }
}
