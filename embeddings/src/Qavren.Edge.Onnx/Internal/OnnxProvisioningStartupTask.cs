using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// Startup order 210. Verifies <b>presence</b> and faults startup when the model is missing. It
/// never downloads: blocking <c>IEdgeHost.Started</c> on a 23 MB transfer also blocks every
/// <c>IEdgeDatabase.OpenConnectionAsync</c>, because every Qavren.Edge entry point awaits the same
/// startup.
/// </summary>
/// <remarks>
/// Takes <see cref="IServiceProvider"/> rather than <see cref="IOnnxSessionHost"/> or
/// <see cref="IOnnxModelStore"/>: <c>EdgeHost</c>'s constructor eagerly resolves every
/// <see cref="IEdgeStartupTask"/>, and the session host depends on <see cref="IEdgeHost"/>, so a
/// direct dependency here is a DI cycle rather than a convenience.
/// </remarks>
internal sealed class OnnxProvisioningStartupTask(IServiceProvider services, string modelId) : IEdgeStartupTask
{
    /// <inheritdoc />
    public int Order => EdgeAiStartupOrder.ModelProvisioning;

    /// <summary>The model this task checks for.</summary>
    public string ModelId => modelId;

    /// <inheritdoc />
    public Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var registration = services.GetServices<OnnxModelRegistration>()
            .FirstOrDefault(r => string.Equals(r.ModelId, modelId, StringComparison.Ordinal))
            ?? throw new EdgeOnnxException(
                EdgeErrorCode.ModelNotRegistered,
                $"ProvisionModelAtStartup('{modelId}') was called but no model is registered under that id.")
            {
                ModelId = modelId,
                Remediation = "Call AddOnnxModel(manifest) for this id, or drop the ProvisionModelAtStartup call.",
            };

        var manifest = registration.Manifest
            ?? throw new EdgeOnnxException(
                EdgeErrorCode.ModelNotRegistered,
                $"'{modelId}' is a fixture registration and carries no manifest, so there is nothing to " +
                "verify the presence of.")
            {
                ModelId = modelId,
            };

        var store = services.GetRequiredService<IOnnxModelStore>();
        if (store.IsProvisioned(manifest))
        {
            return Task.CompletedTask;
        }

        throw new EdgeModelProvisioningException(
            EdgeErrorCode.ModelNotProvisioned,
            modelId,
            manifest.GraphFile,
            $"'{modelId}' is not on disk at startup. ProvisionModelAtStartup verifies presence and " +
            "deliberately does not download: blocking IEdgeHost.Started on a transfer also blocks every " +
            "IEdgeDatabase.OpenConnectionAsync. Bundle the model, side-load it, or drop this call and let " +
            "the first AcquireAsync provision it lazily.")
        {
            ExpectedBytes = manifest.TotalSizeBytes,
        };
    }
}
